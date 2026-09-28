using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DynamoDb.Repository.Aws;
using Microsoft.Extensions.Options;

namespace DynamoDb.Repository.Tests;

public sealed class DynamoDbLocalTests
{
    [DynamoDbLocalFact]
    public async Task Competing_conditional_updates_have_exactly_one_winner()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var cancellation = TestContext.Current.CancellationToken;
        await database.Repository.CreateAsync(new() { Pk = "tenant", Sk = 1, State = HardeningState.Inactive }, cancellation);
        var update = new DynamoDbUpdate<HardeningEntity>().Set(x => x.State, HardeningState.Active);
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            try
            {
                await database.Repository.UpdateAsync("tenant", 1, update, x => x.State == HardeningState.Inactive, cancellation);
                return true;
            }
            catch (DynamoDbConditionFailedException)
            {
                return false;
            }
        });
        Assert.Equal(1, (await Task.WhenAll(attempts)).Count(static succeeded => succeeded));
    }

    [DynamoDbLocalFact]
    public async Task Conditional_writes_and_partial_updates_preserve_concurrent_data()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = database.Repository;
        var cancellation = TestContext.Current.CancellationToken;
        await repository.CreateAsync(new() { Pk = "tenant", Sk = 1, Name = "Ada", State = HardeningState.Inactive }, cancellation);
        await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => repository.CreateAsync(new() { Pk = "tenant", Sk = 1 }, cancellation));

        var update = new DynamoDbUpdate<HardeningEntity>().Set(x => x.State, HardeningState.Active);
        await repository.UpdateAsync("tenant", 1, update, x => x.State == HardeningState.Inactive, cancellation);
        await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => repository.UpdateAsync("tenant", 1, update, x => x.State == HardeningState.Inactive, cancellation));
        await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => repository.UpdateAsync("tenant", 999, update, cancellationToken: cancellation));

        var stored = await repository.GetAsync("tenant", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.Equal("Ada", stored!.Name);
        Assert.Equal(HardeningState.Active, stored.State);
        Assert.Single(await repository.Query.Where(x => x.Pk == "tenant" && x.State == HardeningState.Active).ToListAsync(cancellation));
        await repository.DeleteAsync("tenant", 1, x => x.State == HardeningState.Active, cancellation);
        Assert.Null(await repository.GetAsync("tenant", 1, cancellationToken: cancellation));
    }

    [DynamoDbLocalFact]
    public async Task Range_filters_pagination_and_sparse_indexes_use_valid_service_requests()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = database.Repository;
        var cancellation = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 12; i++)
        {
            await repository.PutAsync(new()
            {
                Pk = "tenant",
                Sk = i,
                Name = $"item-{i}",
                Enabled = i % 2 == 0,
                State = HardeningState.Active,
                IndexKey = i == 2 ? "indexed" : null,
            }, cancellation);
        }
        var query = repository.Query.Where(x => x.Pk == "tenant" && x.Sk >= 3 && x.Sk <= 10 && x.Enabled)
            .OrderBy(x => x.Sk).WithConsistentRead();
        var first = await query.ToPageAsync(2, cancellationToken: cancellation);
        var second = await query.ToPageAsync(2, first.ContinuationToken, cancellation);
        Assert.Equal(new[] { 4, 6 }, first.Items.Select(x => x.Sk));
        Assert.Equal(new[] { 8, 10 }, second.Items.Select(x => x.Sk));
        Assert.Equal(4, await query.CountAsync(cancellation));
        Assert.Equal(2, await query.Take(2).CountAsync(cancellation));
        Assert.True(await query.AnyAsync(cancellation));
        Assert.Equal(4, (await query.FirstOrDefaultAsync(cancellation))!.Sk);
        Assert.Equal(4, await query.Where(x => true).CountAsync(cancellation));
        var empty = Array.Empty<string>();
        Assert.False(await query.Where(x => empty.Contains(x.Name!)).AnyAsync(cancellation));

        var indexed = await repository.Query.UseIndex("sparse-index").Where(x => x.IndexKey == "indexed").ToListAsync(cancellation);
        Assert.Equal(2, Assert.Single(indexed).Sk);
        Assert.Equal(12, await repository.Query.AllowScan().CountAsync(cancellation));
    }

    private sealed class LocalDatabase(AmazonDynamoDBClient client, string tableName, AwsDynamoDbRepository<HardeningEntity> repository) : IAsyncDisposable
    {
        public AwsDynamoDbRepository<HardeningEntity> Repository { get; } = repository;

        public static async Task<LocalDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            var endpoint = new Uri(Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_ENDPOINT")!);
            if (!endpoint.IsLoopback) throw new InvalidOperationException("Integration tests require a loopback DynamoDB Local endpoint.");
            var client = new AmazonDynamoDBClient(new BasicAWSCredentials("localtest", "localtest"),
                new AmazonDynamoDBConfig { ServiceURL = endpoint.ToString(), AuthenticationRegion = "us-east-1" });
            var prefix = $"test-{Guid.NewGuid():N}-";
            var tableName = prefix + "hardening";
            try
            {
                await client.CreateTableAsync(new CreateTableRequest
                {
                    TableName = tableName,
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                    AttributeDefinitions =
                    [
                        new("Pk", ScalarAttributeType.S), new("Sk", ScalarAttributeType.N), new("IndexKey", ScalarAttributeType.S),
                    ],
                    KeySchema = [new("Pk", KeyType.HASH), new("Sk", KeyType.RANGE)],
                    GlobalSecondaryIndexes =
                    [
                        new() { IndexName = "sparse-index", KeySchema = [new("IndexKey", KeyType.HASH)], Projection = new() { ProjectionType = ProjectionType.ALL } },
                    ],
                }, cancellationToken);
                return new LocalDatabase(client, tableName, new(client, Options.Create(new DynamoDbRepositoryOptions
                {
                    TableNamePrefix = prefix,
                    DefaultFetchSize = 3,
                })));
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.DeleteTableAsync(tableName, cleanup.Token);
            }
            finally { client.Dispose(); }
        }
    }
}

public sealed class DynamoDbLocalFactAttribute : FactAttribute
{
    public DynamoDbLocalFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_ENDPOINT")))
        {
            Skip = "Set DYNAMODB_LOCAL_ENDPOINT to run against DynamoDB Local.";
        }
    }
}
