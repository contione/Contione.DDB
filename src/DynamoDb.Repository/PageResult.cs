namespace DynamoDb.Repository;

public sealed record PageResult<TEntity>(
    IReadOnlyList<TEntity> Items,
    string? ContinuationToken,
    int ScannedCount)
{
    public bool HasMore => ContinuationToken is not null;
}