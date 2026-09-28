using Amazon.DynamoDBv2.DataModel;

namespace DynamoDb.Repository.Tests;

[DynamoDBTable("test-entities")]
public sealed class TestEntity
{
    [DynamoDBHashKey("pk")]
    public required string TenantId { get; init; }

    [DynamoDBRangeKey("sk")]
    public required string Id { get; init; }

    [DynamoDBProperty("display_name")]
    public required string Name { get; init; }

    [DynamoDBGlobalSecondaryIndexHashKey("status-created-index", AttributeName = "status")]
    public int Status { get; init; }

    [DynamoDBGlobalSecondaryIndexRangeKey("status-created-index", AttributeName = "created_at")]
    public long CreatedAt { get; init; }

    [DynamoDBProperty("active")]
    public bool Active { get; init; }

    [DynamoDBProperty("tags")]
    public List<string> Tags { get; init; } = [];

    [DynamoDBIgnore]
    public string Ignored => "ignored";
}
