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

    Task DeleteAsync(
        object partitionKey,
        object? sortKey = null,
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