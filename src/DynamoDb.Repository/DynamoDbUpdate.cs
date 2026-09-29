using System.Linq.Expressions;
using System.Numerics;

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

    /// <summary>Atomically adds an amount to a native numeric attribute, starting at zero if absent.</summary>
    /// <remarks>Negative amounts subtract. Existing NULL values are not treated as absent. Increments are not idempotent.</remarks>
    public DynamoDbUpdate<TEntity> Increment<TNumber>(Expression<Func<TEntity, TNumber>> property, TNumber amount)
        where TNumber : struct, INumber<TNumber> => AddIncrement(property, amount);

    /// <summary>Atomically adds an amount to a nullable numeric property whose stored attribute is numeric or absent.</summary>
    /// <remarks>Existing NULL values must be removed first. Custom property converters are not supported.</remarks>
    public DynamoDbUpdate<TEntity> Increment<TNumber>(Expression<Func<TEntity, TNumber?>> property, TNumber amount)
        where TNumber : struct, INumber<TNumber> => AddIncrement(property, amount);

    private DynamoDbUpdate<TEntity> AddIncrement(LambdaExpression property, object amount)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Add(new DynamoDbPropertyUpdate(property, amount, Remove: false) { IsIncrement = true });
    }

    private DynamoDbUpdate<TEntity> Add(DynamoDbPropertyUpdate change) =>
        new(Array.AsReadOnly<DynamoDbPropertyUpdate>([.. _changes, change]));
}

public sealed record DynamoDbPropertyUpdate(LambdaExpression Property, object? Value, bool Remove)
{
    internal bool IsIncrement { get; init; }
}
