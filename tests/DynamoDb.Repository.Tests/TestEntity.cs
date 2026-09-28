using DynamoDb.Repository;

namespace DynamoDb.Repository.Tests;

[DynamoDbTable("test-entities")]
public sealed class TestEntity
{
    [DynamoDbPartitionKey]
    [DynamoDbProperty("pk")]
    public required string TenantId { get; init; }

    [DynamoDbSortKey]
    [DynamoDbProperty("sk")]
    public required string Id { get; init; }

    [DynamoDbProperty("display_name")]
    public required string Name { get; init; }

    [DynamoDbProperty("status")]
    [DynamoDbIndexPartitionKey("status-created-index")]
    public int Status { get; init; }

    [DynamoDbProperty("created_at")]
    [DynamoDbIndexSortKey("status-created-index")]
    public long CreatedAt { get; init; }

    [DynamoDbProperty("active")]
    public bool Active { get; init; }

    [DynamoDbProperty("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [DynamoDbIgnore]
    public string Ignored => "ignored";
}
