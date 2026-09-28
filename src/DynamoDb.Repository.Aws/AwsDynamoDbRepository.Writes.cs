using System.Linq.Expressions;
using System.Reflection;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

public sealed partial class AwsDynamoDbRepository<TEntity>
{
    /// <summary>Creates or fully replaces an item. Use CreateAsync or a condition to prevent overwrites.</summary>
    public Task PutAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        PutCoreAsync(entity, null, createOnly: false, cancellationToken);

    public Task CreateAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        PutCoreAsync(entity, null, createOnly: true, cancellationToken);

    public Task PutAsync(TEntity entity, Expression<Func<TEntity, bool>> condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return PutCoreAsync(entity, condition, createOnly: false, cancellationToken);
    }

    private Task PutCoreAsync(TEntity entity, Expression<Func<TEntity, bool>>? condition, bool createOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var translated = TranslateCondition(condition);
        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = _mapper.ToMap(entity),
            ConditionExpression = translated?.Expression,
            ExpressionAttributeNames = translated?.Names,
            ExpressionAttributeValues = translated?.Values is { Count: > 0 } values ? values : null,
        };
        if (createOnly)
        {
            request.ConditionExpression = "attribute_not_exists(#pk)";
            request.ExpressionAttributeNames = new() { ["#pk"] = _metadata.PrimaryKey.PartitionKey.AttributeName };
        }
        return ExecuteWriteAsync(() => _client.PutItemAsync(request, cancellationToken));
    }

    public Task DeleteAsync(object partitionKey, object? sortKey = null, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(partitionKey, sortKey, null, cancellationToken);

    public Task DeleteAsync(object partitionKey, object? sortKey, Expression<Func<TEntity, bool>> condition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return DeleteCoreAsync(partitionKey, sortKey, condition, cancellationToken);
    }

    private Task DeleteCoreAsync(object partitionKey, object? sortKey, Expression<Func<TEntity, bool>>? condition, CancellationToken cancellationToken)
    {
        var translated = TranslateCondition(condition);
        var request = new DeleteItemRequest
        {
            TableName = _tableName,
            Key = CreateKey(partitionKey, sortKey),
            ConditionExpression = translated?.Expression,
            ExpressionAttributeNames = translated?.Names,
            ExpressionAttributeValues = translated?.Values is { Count: > 0 } values ? values : null,
        };
        return ExecuteWriteAsync(() => _client.DeleteItemAsync(request, cancellationToken));
    }

    public Task UpdateAsync(
        object partitionKey, object? sortKey, DynamoDbUpdate<TEntity> update,
        Expression<Func<TEntity, bool>>? condition = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Changes.Count == 0) throw new ArgumentException("At least one property change is required.", nameof(update));
        var translated = TranslateCondition(condition);
        var names = translated?.Names ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var values = translated?.Values ?? new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var sets = new List<string>();
        var removals = new List<string>();
        foreach (var change in update.Changes)
        {
            var property = GetOrderProperty(change.Property);
            var propertyType = property.Property.PropertyType;
            if (change.Property.ReturnType != propertyType ||
                (change.Value is not null && !(Nullable.GetUnderlyingType(propertyType) ?? propertyType).IsInstanceOfType(change.Value)) ||
                (!change.Remove && change.Value is null && propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) is null))
            {
                throw new ArgumentException("Update selectors must use the property's declared type without conversions.", nameof(update));
            }
            if (property == _metadata.PrimaryKey.PartitionKey || property == _metadata.PrimaryKey.SortKey)
            {
                throw new ArgumentException("Primary key properties cannot be updated.", nameof(update));
            }
            if (!changed.Add(property.AttributeName))
            {
                throw new ArgumentException($"Property '{property.Property.Name}' is updated more than once.", nameof(update));
            }
            var name = $"#u{changed.Count}";
            names[name] = property.AttributeName;
            if (change.Remove)
            {
                removals.Add(name);
                continue;
            }
            if (property.Property.IsDefined(typeof(DynamoDbIndexPartitionKeyAttribute)) ||
                property.Property.IsDefined(typeof(DynamoDbIndexSortKeyAttribute)))
            {
                if (change.Value is null) throw new ArgumentException("Use Remove to clear an index key.", nameof(update));
                values[$":u{changed.Count}"] = ConvertKey(change.Value, property);
            }
            else
            {
                values[$":u{changed.Count}"] = AttributeValueConverter.FromObject(change.Value);
            }
            sets.Add($"{name} = :u{changed.Count}");
        }

        var clauses = new List<string>(2);
        if (sets.Count > 0) clauses.Add($"SET {string.Join(", ", sets)}");
        if (removals.Count > 0) clauses.Add($"REMOVE {string.Join(", ", removals)}");
        names["#existing"] = _metadata.PrimaryKey.PartitionKey.AttributeName;
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = CreateKey(partitionKey, sortKey),
            UpdateExpression = string.Join(" ", clauses),
            ConditionExpression = translated is null
                ? "attribute_exists(#existing)"
                : $"attribute_exists(#existing) AND ({translated.Expression})",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values.Count == 0 ? null : values,
        };
        return ExecuteWriteAsync(() => _client.UpdateItemAsync(request, cancellationToken));
    }

    private TranslatedExpression? TranslateCondition(Expression<Func<TEntity, bool>>? condition) =>
        condition is null ? null : new ExpressionTranslator(_metadata).Translate(condition.Body);

    private static async Task ExecuteWriteAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException exception)
        {
            throw new DynamoDbConditionFailedException(exception);
        }
    }
}
