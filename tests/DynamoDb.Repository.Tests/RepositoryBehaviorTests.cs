using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoDb.Repository.Aws;
using Microsoft.Extensions.Options;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class RepositoryBehaviorTests
{
    [Fact]
    public async Task Key_predicate_executes_query_and_separates_filter()
    {
        var client = new Mock<IAmazonDynamoDB>();
        QueryRequest? captured = null;
        client.Setup(instance => instance.QueryAsync(
                It.IsAny<QueryRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new QueryResponse
            {
                Items = [CreateMap()],
                ScannedCount = 1,
            });
        var repository = CreateRepository(client);

        var result = await repository.Query
            .Where(entity => entity.TenantId == "tenant-1")
            .WhereIf(true, entity => entity.Name == "Ada")
            .OrderByDescending(entity => entity.Id)
            .ToPageAsync(10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.NotNull(captured);
        Assert.Equal("test-test-entities", captured.TableName);
        Assert.NotNull(captured.KeyConditionExpression);
        Assert.NotNull(captured.FilterExpression);
        Assert.False(captured.ScanIndexForward);
        client.Verify(instance => instance.ScanAsync(
            It.IsAny<ScanRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Non_key_predicate_executes_scan()
    {
        var client = new Mock<IAmazonDynamoDB>();
        ScanRequest? captured = null;
        client.Setup(instance => instance.ScanAsync(
                It.IsAny<ScanRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ScanRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new ScanResponse { Items = [], ScannedCount = 0 });
        var repository = CreateRepository(client);

        await repository.Query.Where(entity => entity.Name == "Ada").ToPageAsync(
            10,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(captured?.FilterExpression);
        client.Verify(instance => instance.QueryAsync(
            It.IsAny<QueryRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Page_fills_across_filtered_empty_responses()
    {
        var client = new Mock<IAmazonDynamoDB>();
        client.SetupSequence(instance => instance.ScanAsync(
                It.IsAny<ScanRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResponse
            {
                Items = [],
                ScannedCount = 1,
                LastEvaluatedKey = CreateKey("cursor-1"),
            })
            .ReturnsAsync(new ScanResponse
            {
                Items = [CreateMap()],
                ScannedCount = 1,
            });
        var repository = CreateRepository(client);

        var page = await repository.Query
            .Where(entity => entity.Name.Contains("A"))
            .ToPageAsync(1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(page.Items);
        Assert.Equal(2, page.ScannedCount);
        Assert.Null(page.ContinuationToken);
        client.Verify(instance => instance.ScanAsync(
            It.IsAny<ScanRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Continuation_token_restores_exclusive_start_key()
    {
        var client = new Mock<IAmazonDynamoDB>();
        var requests = new List<ScanRequest>();
        client.Setup(instance => instance.ScanAsync(
                It.IsAny<ScanRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ScanRequest, CancellationToken>((request, _) => requests.Add(request))
            .Returns(() => requests.Count == 1
                ? Task.FromResult(new ScanResponse
                {
                    Items = [CreateMap()],
                    LastEvaluatedKey = CreateKey("cursor-1"),
                })
                : Task.FromResult(new ScanResponse { Items = [CreateMap("account-2")] }));
        var repository = CreateRepository(client);

        var first = await repository.Query.Where(entity => entity.Active).ToPageAsync(
            1,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await repository.Query.Where(entity => entity.Active)
            .ToPageAsync(
                1,
                first.ContinuationToken,
                TestContext.Current.CancellationToken);

        Assert.NotNull(first.ContinuationToken);
        Assert.Equal("cursor-1", requests[1].ExclusiveStartKey["sk"].S);
        Assert.Equal("account-2", second.Items[0].Id);
    }

    [Fact]
    public async Task Count_uses_count_selection()
    {
        var client = new Mock<IAmazonDynamoDB>();
        QueryRequest? captured = null;
        client.Setup(instance => instance.QueryAsync(
                It.IsAny<QueryRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new QueryResponse { Count = 7 });
        var repository = CreateRepository(client);

        var count = await repository.Query
            .Where(entity => entity.TenantId == "tenant-1")
            .CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, count);
        Assert.Equal(Select.COUNT, captured?.Select);
    }

    [Fact]
    public async Task Put_get_and_delete_use_mapped_composite_key()
    {
        var client = new Mock<IAmazonDynamoDB>();
        PutItemRequest? put = null;
        GetItemRequest? get = null;
        DeleteItemRequest? delete = null;
        client.Setup(instance => instance.PutItemAsync(
                It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((request, _) => put = request)
            .ReturnsAsync(new PutItemResponse());
        client.Setup(instance => instance.GetItemAsync(
                It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GetItemRequest, CancellationToken>((request, _) => get = request)
            .ReturnsAsync(new GetItemResponse { Item = CreateMap() });
        client.Setup(instance => instance.DeleteItemAsync(
                It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteItemRequest, CancellationToken>((request, _) => delete = request)
            .ReturnsAsync(new DeleteItemResponse());
        var repository = CreateRepository(client);
        var entity = CreateEntity();

        await repository.PutAsync(entity, TestContext.Current.CancellationToken);
        var restored = await repository.GetAsync(
            "tenant-1",
            "account-1",
            cancellationToken: TestContext.Current.CancellationToken);
        await repository.DeleteAsync(
            "tenant-1",
            "account-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("Ada", put?.Item["display_name"].S);
        Assert.Equal("tenant-1", get?.Key["pk"].S);
        Assert.Equal("account-1", delete?.Key["sk"].S);
        Assert.Equal(entity.Name, restored?.Name);
    }

    [Fact]
    public async Task Take_limits_list_to_requested_count()
    {
        var client = new Mock<IAmazonDynamoDB>();
        QueryRequest? captured = null;
        client.Setup(instance => instance.QueryAsync(
                It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new QueryResponse
            {
                Items = [CreateMap(), CreateMap("account-2")],
                LastEvaluatedKey = CreateKey("cursor"),
            });
        var repository = CreateRepository(client);

        var items = await repository.Query
            .Where(entity => entity.TenantId == "tenant-1")
            .Take(2)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, captured?.Limit);
        client.Verify(instance => instance.QueryAsync(
            It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AwsDynamoDbRepository<TestEntity> CreateRepository(
        Mock<IAmazonDynamoDB> client) => new(
        client.Object,
        Options.Create(new DynamoDbRepositoryOptions
        {
            TableNamePrefix = "test-",
            DefaultFetchSize = 25,
            MaxPageSize = 100,
            AllowScan = true,
        }));

    private static Dictionary<string, AttributeValue> CreateMap(string id = "account-1") => new()
    {
        ["pk"] = new() { S = "tenant-1" },
        ["sk"] = new() { S = id },
        ["display_name"] = new() { S = "Ada" },
        ["status"] = new() { N = "0" },
        ["created_at"] = new() { N = "123" },
        ["active"] = new() { BOOL = true },
        ["tags"] = new() { L = [new AttributeValue { S = "admin" }] },
    };

    private static Dictionary<string, AttributeValue> CreateKey(string id) => new()
    {
        ["pk"] = new() { S = "tenant-1" },
        ["sk"] = new() { S = id },
    };

    private static TestEntity CreateEntity() => new()
    {
        TenantId = "tenant-1",
        Id = "account-1",
        Name = "Ada",
        Status = 0,
        CreatedAt = 123,
        Active = true,
        Tags = ["admin"],
    };
}
