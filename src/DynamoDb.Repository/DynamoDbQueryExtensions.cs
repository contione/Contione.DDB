namespace DynamoDb.Repository;

public static class DynamoDbQueryExtensions
{
    public static Task<PageResult<TEntity>> PageListAsync<TEntity>(
        this IDynamoDbQuery<TEntity> query,
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.ToPageAsync(pageSize, continuationToken, cancellationToken);
    }
}