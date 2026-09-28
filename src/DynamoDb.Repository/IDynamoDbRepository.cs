using System.Linq.Expressions;

namespace DynamoDb.Repository;

public interface IDynamoDbRepository<TEntity> where TEntity : class
{
    IDynamoDbQuery<TEntity> Query { get; }

    Task<TEntity?> GetAsync(
        object partitionKey,
        object? sortKey = null,
        bool consistentRead = false,
        CancellationToken cancellationToken = default);

    Task PutAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Creates an item only if its primary key does not already exist.</summary>
    Task CreateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Replaces an item only when the stored item satisfies the condition.</summary>
    Task PutAsync(
        TEntity entity,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default);

    /// <summary>Updates selected properties of an existing item, optionally checking its stored values.</summary>
    Task UpdateAsync(
        object partitionKey,
        object? sortKey,
        DynamoDbUpdate<TEntity> update,
        Expression<Func<TEntity, bool>>? condition = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        object partitionKey,
        object? sortKey = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        object partitionKey,
        object? sortKey,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default);
}

public interface IDynamoDbQuery<TEntity> where TEntity : class
{
    IDynamoDbQuery<TEntity> Where(Expression<Func<TEntity, bool>> predicate);

    IDynamoDbQuery<TEntity> WhereIf(bool condition, Expression<Func<TEntity, bool>> predicate);

    IDynamoDbQuery<TEntity> UseIndex(string indexName);

    IDynamoDbQuery<TEntity> OrderBy(Expression<Func<TEntity, object?>> keySelector);

    IDynamoDbQuery<TEntity> OrderByDescending(Expression<Func<TEntity, object?>> keySelector);

    IDynamoDbQuery<TEntity> WithConsistentRead(bool enabled = true);

    /// <summary>Explicitly permits a scan for this query when no partition-key equality is available.</summary>
    IDynamoDbQuery<TEntity> AllowScan(bool enabled = true);

    IDynamoDbQuery<TEntity> Take(int count);

    Task<IReadOnlyList<TEntity>> ToListAsync(CancellationToken cancellationToken = default);

    Task<PageResult<TEntity>> ToPageAsync(
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);

    Task<TEntity?> FirstOrDefaultAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
