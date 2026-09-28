using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class ConditionalWriteTests
{
    [Fact]
    public async Task Create_protects_against_overwriting_an_existing_key()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.PutItemAsync(It.Is<PutItemRequest>(r => r.ConditionExpression == "attribute_not_exists(#pk)" && r.ExpressionAttributeNames["#pk"] == "Pk"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutItemResponse());
        await RepositoryHardeningTests.Create(client).CreateAsync(new() { Pk = "tenant", Sk = 1 }, TestContext.Current.CancellationToken);
        client.VerifyAll();
    }

    [Fact]
    public async Task Conditional_failure_is_a_provider_independent_exception()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var providerError = new ConditionalCheckFailedException("conflict");
        client.Setup(x => x.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(providerError);
        var error = await Assert.ThrowsAsync<DynamoDbConditionFailedException>(() => RepositoryHardeningTests.Create(client)
            .PutAsync(new() { Pk = "tenant", Sk = 1 }, x => x.State == HardeningState.Inactive, TestContext.Current.CancellationToken));
        Assert.Same(providerError, error.InnerException);
    }

    [Fact]
    public async Task Partial_update_combines_changes_and_condition_without_token_collisions()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        UpdateItemRequest? captured = null;
        client.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new UpdateItemResponse());
        var update = new DynamoDbUpdate<HardeningEntity>().Set(x => x.State, HardeningState.Active).Remove(x => x.IndexKey);
        await RepositoryHardeningTests.Create(client).UpdateAsync("tenant", 1, update,
            x => x.State == HardeningState.Inactive, TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Contains("SET", captured.UpdateExpression, StringComparison.Ordinal);
        Assert.Contains("REMOVE", captured.UpdateExpression, StringComparison.Ordinal);
        Assert.Contains("attribute_exists", captured.ConditionExpression, StringComparison.Ordinal);
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.S == "Active");
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.S == "Inactive");
        Assert.Equal("1", captured.Key["Sk"].N);
    }

    [Fact]
    public async Task Conditional_delete_passes_condition_to_dynamodb()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.Setup(x => x.DeleteItemAsync(It.Is<DeleteItemRequest>(r => r.ConditionExpression != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteItemResponse());
        await RepositoryHardeningTests.Create(client).DeleteAsync("tenant", 1, x => x.Enabled, TestContext.Current.CancellationToken);
        client.Verify(x => x.DeleteItemAsync(It.IsAny<DeleteItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Updates_reject_key_changes_duplicates_and_empty_changes()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        var repository = RepositoryHardeningTests.Create(client);
        var changes = new[]
        {
            new DynamoDbUpdate<HardeningEntity>(),
            new DynamoDbUpdate<HardeningEntity>().Set(x => x.Pk, "other"),
            new DynamoDbUpdate<HardeningEntity>().Set(x => x.Enabled, true).Remove(x => x.Enabled),
            new DynamoDbUpdate<HardeningEntity>().Set(x => x.IndexKey, (string?)null),
            new DynamoDbUpdate<HardeningEntity>().Set(x => (int)x.State, 1),
        };
        foreach (var update in changes)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync("tenant", 1, update, cancellationToken: TestContext.Current.CancellationToken));
        }
        client.VerifyNoOtherCalls();
    }

    [Fact]
    public void Update_builder_can_be_reused_without_mutating_its_base()
    {
        var original = new DynamoDbUpdate<HardeningEntity>().Set(x => x.Enabled, true);
        var derived = original.Remove(x => x.IndexKey);
        Assert.Single(original.Changes);
        Assert.Equal(2, derived.Changes.Count);
    }
}
