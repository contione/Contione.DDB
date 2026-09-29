using Amazon.DynamoDBv2.DataModel;

namespace DynamoDb.Repository.Tests;

[DynamoDBTable("hardening")]
public sealed class CounterEntity
{
    [DynamoDBHashKey]
    public string Pk { get; init; } = string.Empty;

    [DynamoDBRangeKey]
    public int Sk { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public long LastEditTime { get; init; }

    public decimal Amount { get; init; }

    public long? OptionalTokens { get; init; }
}
