using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;

namespace DynamoDb.Repository.Tests;

public sealed class BatchWriteTests
{
    [Fact]
    public async Task Sdk_splits_more_than_25_writes_and_uses_the_prefixed_table()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        var items = Enumerable.Range(1, 26).Select(i => new HardeningEntity { Pk = "tenant", Sk = i, Name = "Ada" });

        await repository.BatchWriteAsync(putItems: items, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, request => Assert.InRange(request.RequestItems["test-hardening"].Count, 1, 25));
        var writes = client.Requests.SelectMany(request => request.RequestItems["test-hardening"]).ToList();
        Assert.Equal(26, writes.Count);
        Assert.Equal(26, writes.Select(write => write.PutRequest.Item["Sk"].N).Distinct().Count());
        Assert.All(writes, write => Assert.Equal("Ada", write.PutRequest.Item["display_name"].S));
    }

    [Fact]
    public async Task Sdk_retries_only_unprocessed_items()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        client.Send = (request, _) => Task.FromResult(client.Requests.Count == 1
            ? new BatchWriteItemResponse { UnprocessedItems = new() { ["test-hardening"] = [request.RequestItems["test-hardening"][1]] } }
            : new BatchWriteItemResponse());

        await repository.BatchWriteAsync(
            putItems: [new() { Pk = "tenant", Sk = 1 }, new() { Pk = "tenant", Sk = 2 }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal("2", Assert.Single(client.Requests[1].RequestItems["test-hardening"]).PutRequest.Item["Sk"].N);
    }

    [Fact]
    public async Task Missing_and_empty_inputs_do_not_send_requests()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        await repository.BatchWriteAsync(cancellationToken: TestContext.Current.CancellationToken);
        await repository.BatchWriteAsync([], [], TestContext.Current.CancellationToken);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Precancelled_operation_does_not_send_requests()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.BatchWriteAsync(
            putItems: [new() { Pk = "tenant", Sk = 1 }], cancellationToken: cancellation.Token));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_and_stops_unprocessed_item_retries()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        client.Send = (request, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            token.ThrowIfCancellationRequested();
            cancellation.Cancel();
            return Task.FromResult(new BatchWriteItemResponse { UnprocessedItems = request.RequestItems });
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.BatchWriteAsync(
            putItems: [new() { Pk = "tenant", Sk = 1 }], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Sdk_failures_are_not_reported_as_success()
    {
        using var client = new BatchClient();
        using var repository = Repository(client);
        var failure = new ResourceNotFoundException("missing table");
        client.Send = (_, _) => Task.FromException<BatchWriteItemResponse>(failure);
        var actual = await Assert.ThrowsAsync<ResourceNotFoundException>(() => repository.BatchWriteAsync(
            deleteItems: [new() { Pk = "tenant", Sk = 1 }], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Same(failure, actual);
    }

    private static DynamoDbRepository<HardeningEntity> Repository(BatchClient client) =>
        new(client, Options.Create(new DynamoDbRepositoryOptions { TableNamePrefix = "test-" }));

    // Retain the concrete SDK client metadata behavior while intercepting network calls.
    private sealed class BatchClient() : AmazonDynamoDBClient(new BasicAWSCredentials("test", "test"),
        new AmazonDynamoDBConfig { ServiceURL = "http://localhost:1", AuthenticationRegion = "us-east-1" })
    {
        public List<BatchWriteItemRequest> Requests { get; } = [];
        public Func<BatchWriteItemRequest, CancellationToken, Task<BatchWriteItemResponse>>? Send { get; set; }

        public override Task<BatchWriteItemResponse> BatchWriteItemAsync(BatchWriteItemRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(new BatchWriteItemRequest
            {
                RequestItems = request.RequestItems.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()),
            });
            return Send?.Invoke(request, cancellationToken) ?? Task.FromResult(new BatchWriteItemResponse());
        }
    }
}
