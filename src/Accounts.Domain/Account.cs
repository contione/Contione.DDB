using DynamoDb.Repository;

namespace Accounts.Domain;

[DynamoDbTable("accounts")]
public sealed class Account
{
    [DynamoDbPartitionKey]
    [DynamoDbProperty("tenant_id")]
    public required string TenantId { get; init; }

    [DynamoDbSortKey]
    [DynamoDbProperty("account_id")]
    public required string AccountId { get; init; }

    [DynamoDbProperty("name")]
    public required string Name { get; set; }

    [DynamoDbProperty("status")]
    [DynamoDbIndexPartitionKey(IndexNames.StatusCreatedAt)]
    public int Status { get; set; }

    [DynamoDbProperty("created_at")]
    [DynamoDbIndexSortKey(IndexNames.StatusCreatedAt)]
    public DateTimeOffset CreatedAt { get; init; }

    [DynamoDbProperty("email")]
    public string? Email { get; set; }
}

public static class IndexNames
{
    public const string StatusCreatedAt = "status-created-at-index";
}