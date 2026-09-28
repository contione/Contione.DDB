namespace DynamoDb.Repository;

public sealed record PageResult<TEntity>(
    IReadOnlyList<TEntity> Items,
    string? ContinuationToken,
    int ScannedCount)
{
    public bool HasMore => ContinuationToken is not null;

    /// <summary>SDK calls used to produce this page, excluding SDK retries.</summary>
    public int RequestCount { get; init; }
}
