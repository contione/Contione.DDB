using System.Linq.Expressions;

namespace DynamoDb.Repository;

/// <summary>An immutable set of top-level property changes. Keys cannot be updated.</summary>
public sealed class DynamoDbUpdate<TEntity> where TEntity : class
{
    private readonly IReadOnlyList<DynamoDbPropertyUpdate> _changes;

    public DynamoDbUpdate() : this([]) { }

    private DynamoDbUpdate(IReadOnlyList<DynamoDbPropertyUpdate> changes) => _changes = changes;

    public IReadOnlyList<DynamoDbPropertyUpdate> Changes => _changes;

    public DynamoDbUpdate<TEntity> Set<TValue>(Expression<Func<TEntity, TValue>> property, TValue value)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Add(new DynamoDbPropertyUpdate(property, value, Remove: false));
    }

    public DynamoDbUpdate<TEntity> Remove<TValue>(Expression<Func<TEntity, TValue>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Add(new DynamoDbPropertyUpdate(property, null, Remove: true));
    }

    private DynamoDbUpdate<TEntity> Add(DynamoDbPropertyUpdate change) =>
        new(Array.AsReadOnly<DynamoDbPropertyUpdate>([.. _changes, change]));
}

public sealed record DynamoDbPropertyUpdate(LambdaExpression Property, object? Value, bool Remove);
