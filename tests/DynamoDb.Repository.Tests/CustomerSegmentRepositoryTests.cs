using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class CustomerSegmentRepositoryTests
{
    [Fact]
    public async Task Queries_only_segment_ids_and_follows_dynamodb_pagination()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        QueryRequest? firstRequest = null;
        QueryRequest? secondRequest = null;
        var firstPageKey = new Dictionary<string, AttributeValue>
        {
            ["customer_id"] = new() { S = "customer-1" },
            ["segment_id"] = new() { S = "segment-1" },
        };
        var callCount = 0;
        client.Setup(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QueryRequest, CancellationToken>((request, _) =>
            {
                if (callCount++ == 0) firstRequest = request;
                else secondRequest = request;
            })
            .ReturnsAsync(() => callCount == 1
                ? new QueryResponse
                {
                    Items = [new() { ["segment_id"] = new() { S = "segment-1" } }],
                    LastEvaluatedKey = firstPageKey,
                }
                : new QueryResponse
                {
                    Items = [new() { ["segment_id"] = new() { S = "segment-2" } }],
                });

        var repository = new CustomerSegmentRepository(client.Object, "customer-segments", "customer_id", "segment_id");
        var result = await repository.GetSegmentIdsAsync("customer-1", TestContext.Current.CancellationToken);

        Assert.Equal(["segment-1", "segment-2"], result);
        Assert.NotNull(firstRequest);
        Assert.Equal("customer-segments", firstRequest.TableName);
        Assert.Equal("#customerId = :customerId", firstRequest.KeyConditionExpression);
        Assert.Equal("#segmentId", firstRequest.ProjectionExpression);
        Assert.False(firstRequest.ConsistentRead);
        Assert.Null(firstRequest.ExclusiveStartKey);
        Assert.Equal("customer_id", firstRequest.ExpressionAttributeNames["#customerId"]);
        Assert.Equal("segment_id", firstRequest.ExpressionAttributeNames["#segmentId"]);
        Assert.Equal("customer-1", firstRequest.ExpressionAttributeValues[":customerId"].S);
        Assert.NotNull(secondRequest);
        Assert.Same(firstPageKey, secondRequest.ExclusiveStartKey);
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Rejects_empty_customer_ids_without_calling_dynamodb()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var repository = new CustomerSegmentRepository(client.Object, "customer-segments", "customer_id", "segment_id");

        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetSegmentIdsAsync(" ", TestContext.Current.CancellationToken));
        client.Verify(x => x.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
