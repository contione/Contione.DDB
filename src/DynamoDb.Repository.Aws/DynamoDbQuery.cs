using System.Linq.Expressions;

namespace DynamoDb.Repository.Aws;

internal sealed record QueryState<TEntity>(
    IReadOnlyList<Expression<Func<TEntity, bool>>> Predicates,
    string? IndexName = null,
    PropertyMetadata? OrderProperty = null,
    bool Descending = false,
    bool ConsistentRead = false,
    int? Take = null,
    bool? AllowScan = null)
    where TEntity : class
{
    public static QueryState<TEntity> Empty { get; } = new([]);
}

internal sealed class DynamoDbQuery<TEntity>(
    AwsDynamoDbRepository<TEntity> repository,
    QueryState<TEntity>? state = null) : IDynamoDbQuery<TEntity>
    where TEntity : class
{
    private readonly QueryState<TEntity> _state = state ?? QueryState<TEntity>.Empty;

    public IDynamoDbQuery<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return New(_state with { Predicates = [.. _state.Predicates, predicate] });
    }

    public IDynamoDbQuery<TEntity> WhereIf(
        bool condition,
        Expression<Func<TEntity, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return condition ? Where(predicate) : this;
    }

    public IDynamoDbQuery<TEntity> UseIndex(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        _ = EntityMetadata.For<TEntity>().GetKeySchema(indexName);
        return New(_state with { IndexName = indexName });
    }

    public IDynamoDbQuery<TEntity> OrderBy(Expression<Func<TEntity, object?>> keySelector) =>
        SetOrder(keySelector, descending: false);

    public IDynamoDbQuery<TEntity> OrderByDescending(Expression<Func<TEntity, object?>> keySelector) =>
        SetOrder(keySelector, descending: true);

    public IDynamoDbQuery<TEntity> WithConsistentRead(bool enabled = true) =>
        New(_state with { ConsistentRead = enabled });

    public IDynamoDbQuery<TEntity> AllowScan(bool enabled = true) =>
        New(_state with { AllowScan = enabled });

    public IDynamoDbQuery<TEntity> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return New(_state with { Take = count });
    }

    public Task<IReadOnlyList<TEntity>> ToListAsync(CancellationToken cancellationToken = default) =>
        repository.ExecuteListAsync(_state, cancellationToken);

    public Task<PageResult<TEntity>> ToPageAsync(
        int pageSize,
        string? continuationToken = null,
        CancellationToken cancellationToken = default) =>
        repository.ExecutePageAsync(_state, pageSize, continuationToken, cancellationToken);

    public Task<TEntity?> FirstOrDefaultAsync(CancellationToken cancellationToken = default) =>
        repository.ExecuteFirstAsync(_state, cancellationToken);

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        await repository.ExecuteCountAsync(_state, existsOnly: true, cancellationToken).ConfigureAwait(false) > 0;

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        repository.ExecuteCountAsync(_state, existsOnly: false, cancellationToken);

    private IDynamoDbQuery<TEntity> SetOrder(
        Expression<Func<TEntity, object?>> keySelector,
        bool descending)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        var property = repository.GetOrderProperty(keySelector);
        return New(_state with { OrderProperty = property, Descending = descending });
    }

    private DynamoDbQuery<TEntity> New(QueryState<TEntity> newState) => new(repository, newState);
}
