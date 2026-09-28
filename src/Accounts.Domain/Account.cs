using Amazon.DynamoDBv2.DataModel;

namespace Accounts.Domain;

[DynamoDBTable("accounts")]
public sealed class Account
{
    [DynamoDBHashKey("tenant_id")]
    public required string TenantId { get; init; }

    [DynamoDBRangeKey("account_id")]
    public required string AccountId { get; init; }

    [DynamoDBProperty("name")]
    public required string Name { get; set; }

    [DynamoDBGlobalSecondaryIndexHashKey(IndexNames.StatusCreatedAt, AttributeName = "status")]
    public int Status { get; set; }

    [DynamoDBGlobalSecondaryIndexRangeKey(IndexNames.StatusCreatedAt, AttributeName = "created_at", Converter = typeof(UtcDateTimeOffsetConverter))]
    public DateTimeOffset CreatedAt { get; init; }

    [DynamoDBProperty("email")]
    public string? Email { get; set; }
}

public static class IndexNames
{
    public const string StatusCreatedAt = "status-created-at-index";
}
