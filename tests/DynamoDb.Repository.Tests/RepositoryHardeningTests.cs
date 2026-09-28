using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoDb.Repository.Aws;
using Microsoft.Extensions.Options;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class RepositoryHardeningTests
{
    [Fact]
    public async Task Scan_requires_explicit_permission()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var repository = Create(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.Query.ToListAsync(TestContext.Current.CancellationToken));
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existence_and_first_fetch_in_batches_before_filtering(bool existsOnly)
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var requests = new List<QueryRequest>();
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns((QueryRequest request, CancellationToken _) =>
            {
                requests.Add(request);
                return Task.FromResult(requests.Count == 1
                    ? new QueryResponse { Count = 0, ScannedCount = 100, LastEvaluatedKey = Key(100) }
                    : new QueryResponse { Count = 1, ScannedCount = 1, Items = [Map(101)] });
            });
        var query = Create(client).Query.Where(x => x.Pk == "tenant" && x.Enabled);
        if (existsOnly) Assert.True(await query.AnyAsync(TestContext.Current.CancellationToken));
        else Assert.Equal(101, (await query.FirstOrDefaultAsync(TestContext.Current.CancellationToken))!.Sk);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(100, request.Limit));
        Assert.Equal("100", requests[1].ExclusiveStartKey["Sk"].N);
    }

    [Fact]
    public async Task List_fetch_size_is_independent_of_public_page_size()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.Is<QueryRequest>(r => r.Limit == 100), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Map(1)], ScannedCount = 1 });
        var repository = Create(client, new() { DefaultFetchSize = 100, MaxPageSize = 50 });
        Assert.Single(await repository.Query.Where(x => x.Pk == "tenant").ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Large_page_limit_does_not_preallocate_the_entire_page()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [], ScannedCount = 0 });
        var repository = Create(client, new() { MaxPageSize = int.MaxValue });
        var page = await repository.Query.Where(x => x.Pk == "tenant")
            .ToPageAsync(int.MaxValue, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.RequestCount);
    }

    [Fact]
    public async Task Request_budget_applies_to_the_entire_list_operation()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Map(1)], LastEvaluatedKey = Key(1), ScannedCount = 1 });
        var repository = Create(client, new() { DefaultFetchSize = 1, MaxRequestsPerOperation = 2 });
        var error = await Assert.ThrowsAsync<DynamoDbQueryLimitExceededException>(() =>
            repository.Query.Where(x => x.Pk == "tenant").ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, error.Requests);
        Assert.Equal(2, error.EvaluatedItems);
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Evaluated_item_budget_clamps_request_limit()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.Is<QueryRequest>(r => r.Limit == 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Count = 0, ScannedCount = 5, LastEvaluatedKey = Key(5) });
        var repository = Create(client, new() { MaxEvaluatedItems = 5 });
        var error = await Assert.ThrowsAsync<DynamoDbQueryLimitExceededException>(() =>
            repository.Query.Where(x => x.Pk == "tenant" && x.Enabled).AnyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, error.EvaluatedItems);
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_sdk_request()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), cancellation.Token))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult(new QueryResponse { LastEvaluatedKey = Key(1) });
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(client).Query.Where(x => x.Pk == "tenant").ToListAsync(cancellation.Token));
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Continuation_token_is_bound_to_the_query_values()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Items = [Map(1)], ScannedCount = 1, LastEvaluatedKey = Key(1) });
        var repository = Create(client);
        var page = await repository.Query.Where(x => x.Pk == "tenant").ToPageAsync(1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, page.RequestCount);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.Query.Where(x => x.Pk == "another-tenant")
            .ToPageAsync(1, page.ContinuationToken, TestContext.Current.CancellationToken));
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Take_with_pagination_is_rejected_instead_of_restarting_the_limit()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(client).Query.Take(2)
            .ToPageAsync(1, cancellationToken: TestContext.Current.CancellationToken));
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public void Mapper_round_trips_enum_binary_and_json_property_names()
    {
        var mapper = new EntityMapper<HardeningEntity>();
        var entity = new HardeningEntity { Pk = "tenant", Sk = 1, State = HardeningState.Active, Name = "Ada", Data = [1, 2, 3] };
        var restored = mapper.FromMap(mapper.ToMap(entity));
        Assert.Equal(entity.State, restored.State);
        Assert.Equal(entity.Name, restored.Name);
        Assert.Equal(entity.Data, restored.Data);
    }

    [Fact]
    public void Null_secondary_key_is_omitted_for_sparse_indexes()
    {
        var map = new EntityMapper<HardeningEntity>().ToMap(new() { Pk = "tenant", Sk = 1 });
        Assert.DoesNotContain("IndexKey", map.Keys);
    }

    [Fact]
    public void Binary_set_materializes_without_data_loss()
    {
        var map = Map(1);
        map["Blobs"] = new AttributeValue { BS = [new MemoryStream([1, 2]), new MemoryStream([3])] };
        var entity = new EntityMapper<HardeningEntity>().FromMap(map);
        Assert.Equal(2, entity.Blobs.Count);
        Assert.Equal(new byte[] { 1, 2 }, entity.Blobs[0]);
    }

    [Fact]
    public void Public_indexers_are_not_mapped()
    {
        Assert.DoesNotContain(EntityMetadata.For<HardeningEntity>().Properties, property => property.Property.Name == "Item");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_primary_keys_are_rejected(string? key)
    {
        Assert.Throws<ArgumentException>(() => new EntityMapper<HardeningEntity>().ToMap(new() { Pk = key!, Sk = 1 }));
    }

    [Fact]
    public void Non_finite_numbers_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AttributeValueConverter.FromObject(double.NaN));
    }

    [Fact]
    public void Key_length_is_validated_in_utf8_bytes()
    {
        var mapper = new EntityMapper<HardeningEntity>();
        Assert.Throws<ArgumentException>(() => mapper.ToMap(new() { Pk = new string('界', 683), Sk = 1 }));
    }

    internal static AwsDynamoDbRepository<HardeningEntity> Create(Mock<IAmazonDynamoDB> client, DynamoDbRepositoryOptions? options = null) =>
        new(client.Object, Options.Create(options ?? new()));

    internal static Dictionary<string, AttributeValue> Key(int sortKey) => new()
    {
        ["Pk"] = new() { S = "tenant" },
        ["Sk"] = new() { N = sortKey.ToString(System.Globalization.CultureInfo.InvariantCulture) },
    };

    internal static Dictionary<string, AttributeValue> Map(int sortKey)
    {
        var map = Key(sortKey);
        map["Enabled"] = new() { BOOL = true };
        return map;
    }
}

public enum HardeningState { Inactive, Active }

[DynamoDbTable("hardening")]
public sealed class HardeningEntity
{
    [DynamoDbPartitionKey] public string Pk { get; init; } = string.Empty;
    [DynamoDbSortKey] public int Sk { get; init; }
    public HardeningState State { get; init; }
    public bool Enabled { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    public byte[]? Data { get; init; }
    public IReadOnlyList<byte[]> Blobs { get; init; } = [];
    [DynamoDbIndexPartitionKey("sparse-index")] public string? IndexKey { get; init; }
    public string this[int index] => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
