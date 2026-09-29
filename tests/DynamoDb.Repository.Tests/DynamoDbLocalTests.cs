using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using DynamoDb.Repository;
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

    [DynamoDbLocalFact]
    public async Task Batch_write_splits_mixed_operations_and_replaces_complete_items()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = database.Repository;
        var cancellation = TestContext.Current.CancellationToken;

        await repository.PutAsync(new()
        {
            Pk = "replace-null",
            Sk = 1,
            Name = "old-name",
            Enabled = true,
            State = HardeningState.Inactive,
            IndexKey = null,
        }, cancellation);
        await repository.PutAsync(new()
        {
            Pk = "replace-value",
            Sk = 1,
            Name = "old-value",
            IndexKey = null,
        }, cancellation);
        await repository.PutAsync(new()
        {
            Pk = "delete",
            Sk = 1,
            Name = "to-delete",
            IndexKey = null,
        }, cancellation);

        var putItems = Enumerable.Range(0, 26).Select(index => new HardeningEntity
        {
            Pk = index switch
            {
                0 => "replace-null",
                1 => "replace-value",
                _ => "batch",
            },
            Sk = index < 2 ? 1 : index - 1,
            Name = index switch
            {
                0 => null,
                1 => "new-value",
                _ => $"batch-{index - 1}",
            },
            State = HardeningState.Active,
            Enabled = false,
            IndexKey = null,
        }).ToArray();

        await repository.BatchWriteAsync(
            putItems,
            [new HardeningEntity { Pk = "delete", Sk = 1, IndexKey = null }],
            cancellation);

        var replacedWithNull = await repository.GetAsync("replace-null", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.NotNull(replacedWithNull);
        Assert.Null(replacedWithNull.Name);
        Assert.False(replacedWithNull.Enabled);
        Assert.Equal(HardeningState.Active, replacedWithNull.State);
        var replacedWithValue = await repository.GetAsync("replace-value", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.NotNull(replacedWithValue);
        Assert.Equal("new-value", replacedWithValue.Name);
        Assert.False(replacedWithValue.Enabled);
        Assert.Equal(24, await repository.Query.Where(item => item.Pk == "batch").CountAsync(cancellation));
        Assert.Equal("batch-1", (await repository.GetAsync("batch", 1, cancellationToken: cancellation))!.Name);
        Assert.Null(await repository.GetAsync("delete", 1, cancellationToken: cancellation));
    }

    [DynamoDbLocalFact]
    public async Task Batch_write_supports_delete_only_batches()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var repository = database.Repository;
        var cancellation = TestContext.Current.CancellationToken;
        var items = new[]
        {
            new HardeningEntity { Pk = "delete-only", Sk = 1, Name = "one", IndexKey = null },
            new HardeningEntity { Pk = "delete-only", Sk = 2, Name = "two", IndexKey = null },
        };

        await repository.BatchWriteAsync(putItems: items, cancellationToken: cancellation);
        await repository.BatchWriteAsync(
            deleteItems: items.Select(item => new HardeningEntity
            {
                Pk = item.Pk,
                Sk = item.Sk,
                IndexKey = null,
            }),
            cancellationToken: cancellation);

        Assert.Null(await repository.GetAsync("delete-only", 1, cancellationToken: cancellation));
        Assert.Null(await repository.GetAsync("delete-only", 2, cancellationToken: cancellation));
    }

    [DynamoDbLocalFact]
    public async Task Increment_initializes_missing_fields_and_combines_set_operations()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        using var repository = database.CreateRepository<CounterEntity>();
        var cancellation = TestContext.Current.CancellationToken;
        await repository.PutAsync(new CounterEntity
        {
            Pk = "counter",
            Sk = 1,
            InputTokens = 10,
            OutputTokens = 20,
            TotalTokens = 30,
            LastEditTime = 1,
            Amount = 1.25m,
            OptionalTokens = 9,
        }, cancellation);

        await repository.UpdateAsync(
            "counter",
            1,
            new DynamoDbUpdate<CounterEntity>()
                .Remove(item => item.InputTokens)
                .Remove(item => item.Amount)
                .Remove(item => item.OptionalTokens),
            cancellationToken: cancellation);

        var editedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var update = new DynamoDbUpdate<CounterEntity>()
            .Increment(item => item.InputTokens, 5L)
            .Increment(item => item.OutputTokens, -2L)
            .Increment(item => item.TotalTokens, 0L)
            .Increment(item => item.Amount, 2.75m)
            .Increment(item => item.OptionalTokens, 4L)
            .Set(item => item.LastEditTime, editedAt);
        await repository.UpdateAsync("counter", 1, update, cancellationToken: cancellation);

        var stored = await repository.GetAsync("counter", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.NotNull(stored);
        Assert.Equal(5L, stored.InputTokens);
        Assert.Equal(18L, stored.OutputTokens);
        Assert.Equal(30L, stored.TotalTokens);
        Assert.Equal(editedAt, stored.LastEditTime);
        Assert.Equal(2.75m, stored.Amount);
        Assert.Equal(4L, stored.OptionalTokens);

        await repository.UpdateAsync(
            "counter",
            1,
            new DynamoDbUpdate<CounterEntity>()
                .Increment(item => item.InputTokens, -2L)
                .Increment(item => item.Amount, -0.75m)
                .Increment(item => item.OptionalTokens, -4L),
            cancellationToken: cancellation);

        stored = await repository.GetAsync("counter", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.NotNull(stored);
        Assert.Equal(3L, stored.InputTokens);
        Assert.Equal(2m, stored.Amount);
        Assert.Equal(0L, stored.OptionalTokens);
    }

    [DynamoDbLocalFact]
    public async Task Concurrent_increments_are_atomic_and_conditions_are_enforced()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        using var repository = database.CreateRepository<CounterEntity>();
        var cancellation = TestContext.Current.CancellationToken;
        await repository.PutAsync(new CounterEntity { Pk = "concurrent", Sk = 1 }, cancellation);

        var attempts = Enumerable.Range(0, 8).Select(_ => repository.UpdateAsync(
            "concurrent",
            1,
            new DynamoDbUpdate<CounterEntity>().Increment(item => item.TotalTokens, 1L),
            cancellationToken: cancellation));
        await Task.WhenAll(attempts);

        var stored = await repository.GetAsync("concurrent", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.Equal(8L, stored!.TotalTokens);

        await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => repository.UpdateAsync(
            "concurrent",
            1,
            new DynamoDbUpdate<CounterEntity>().Increment(item => item.TotalTokens, 100L),
            item => item.TotalTokens == 0L,
            cancellation));
        stored = await repository.GetAsync("concurrent", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.Equal(8L, stored!.TotalTokens);
    }

    [DynamoDbLocalFact]
    public async Task Increment_rejects_missing_records()
    {
        await using var database = await LocalDatabase.CreateAsync(TestContext.Current.CancellationToken);
        using var repository = database.CreateRepository<CounterEntity>();

        await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => repository.UpdateAsync(
            "missing",
            1,
            new DynamoDbUpdate<CounterEntity>().Increment(item => item.InputTokens, 1L),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [DynamoDbLocalFact]
    public async Task Increment_does_not_treat_stored_null_as_a_missing_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var database = await LocalDatabase.CreateAsync(cancellation);
        using var repository = database.CreateRepository<CounterEntity>();
        await repository.PutAsync(new CounterEntity { Pk = "null-counter", Sk = 1 }, cancellation);
        await repository.UpdateAsync("null-counter", 1,
            new DynamoDbUpdate<CounterEntity>().Set(x => x.OptionalTokens, (long?)null),
            cancellationToken: cancellation);

        var exception = await Assert.ThrowsAsync<AmazonDynamoDBException>(() => repository.UpdateAsync(
            "null-counter", 1,
            new DynamoDbUpdate<CounterEntity>().Increment(x => x.OptionalTokens, 1L).Set(x => x.LastEditTime, 100L),
            cancellationToken: cancellation));

        Assert.Equal("ValidationException", exception.ErrorCode);
        var stored = await repository.GetAsync("null-counter", 1, consistentRead: true, cancellationToken: cancellation);
        Assert.NotNull(stored);
        Assert.Null(stored.OptionalTokens);
        Assert.Equal(0L, stored.LastEditTime);
    }

    private sealed class LocalDatabase(AmazonDynamoDBClient client, string tableName, DynamoDbRepository<HardeningEntity> repository) : IAsyncDisposable
    {
        public DynamoDbRepository<HardeningEntity> Repository { get; } = repository;

        public DynamoDbRepository<TEntity> CreateRepository<TEntity>() where TEntity : class =>
            new(client, Options.Create(new DynamoDbRepositoryOptions
            {
                TableNamePrefix = tableName[..^"hardening".Length],
                DefaultFetchSize = 3,
            }));

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
