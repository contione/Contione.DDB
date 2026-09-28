using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace DynamoDb.Repository.Aws;

internal sealed record PropertyMetadata(
    PropertyInfo Property,
    string AttributeName,
    Func<object, object?> GetValue,
    string JsonName,
    bool IsPrimaryKey,
    bool IsIndexKey,
    int KeySizeLimit);

internal sealed record KeySchema(PropertyMetadata PartitionKey, PropertyMetadata? SortKey);

internal sealed class EntityMetadata
{
    private static readonly ConcurrentDictionary<Type, EntityMetadata> Cache = new();
    private readonly IReadOnlyDictionary<string, PropertyMetadata> _propertiesByClrName;
    private readonly IReadOnlyDictionary<string, KeySchema> _indexes;

    private EntityMetadata(Type entityType)
    {
        TableName = entityType.GetCustomAttribute<DynamoDbTableAttribute>()?.Name
            ?? throw new InvalidOperationException(
                $"Entity '{entityType.Name}' must declare {nameof(DynamoDbTableAttribute)}.");

        Properties = entityType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetMethod is { IsPublic: true } &&
                property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<DynamoDbIgnoreAttribute>() is null)
            .Select(CreateProperty)
            .ToArray();

        if (Properties.Select(static property => property.AttributeName).Distinct(StringComparer.Ordinal).Count() != Properties.Count)
        {
            throw new InvalidOperationException($"Entity '{entityType.Name}' maps multiple properties to the same attribute.");
        }

        _propertiesByClrName = Properties.ToDictionary(
            static property => property.Property.Name,
            StringComparer.Ordinal);

        var partitionKeys = Properties
            .Where(static property => property.Property.IsDefined(typeof(DynamoDbPartitionKeyAttribute)))
            .ToArray();
        var sortKeys = Properties
            .Where(static property => property.Property.IsDefined(typeof(DynamoDbSortKeyAttribute)))
            .ToArray();

        if (partitionKeys.Length != 1 || sortKeys.Length > 1)
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.Name}' must have exactly one partition key and at most one sort key.");
        }

        PrimaryKey = new KeySchema(partitionKeys[0], sortKeys.SingleOrDefault());
        if (PrimaryKey.PartitionKey == PrimaryKey.SortKey)
        {
            throw new InvalidOperationException("Partition and sort keys must be different properties.");
        }
        _indexes = BuildIndexes(Properties);
    }

    public string TableName { get; }

    public IReadOnlyList<PropertyMetadata> Properties { get; }

    public KeySchema PrimaryKey { get; }

    public static EntityMetadata For<TEntity>() where TEntity : class =>
        Cache.GetOrAdd(typeof(TEntity), static type => new EntityMetadata(type));

    public PropertyMetadata GetProperty(MemberInfo member) =>
        _propertiesByClrName.TryGetValue(member.Name, out var property)
            ? property
            : throw new NotSupportedException($"Property '{member.Name}' is not mapped to DynamoDB.");

    public KeySchema GetKeySchema(string? indexName)
    {
        if (indexName is null)
        {
            return PrimaryKey;
        }

        return _indexes.TryGetValue(indexName, out var schema)
            ? schema
            : throw new InvalidOperationException(
                $"Index '{indexName}' is not mapped on entity metadata.");
    }

    private static PropertyMetadata CreateProperty(PropertyInfo property)
    {
        if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
        {
            throw new InvalidOperationException($"Mapped property '{property.Name}' cannot use JsonIgnore. Use DynamoDbIgnore instead.");
        }
        var primary = property.IsDefined(typeof(DynamoDbPartitionKeyAttribute)) || property.IsDefined(typeof(DynamoDbSortKeyAttribute));
        var indexed = property.IsDefined(typeof(DynamoDbIndexPartitionKeyAttribute)) || property.IsDefined(typeof(DynamoDbIndexSortKeyAttribute));
        var sortKey = property.IsDefined(typeof(DynamoDbSortKeyAttribute)) || property.IsDefined(typeof(DynamoDbIndexSortKeyAttribute));
        if ((primary || indexed) && !AttributeValueConverter.IsKeyType(property.PropertyType))
        {
            throw new InvalidOperationException($"Key '{property.Name}' must map to a DynamoDB string, number or binary value.");
        }
        var instance = Expression.Parameter(typeof(object), "instance");
        var getter = Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                typeof(object)),
            instance).Compile();

        return new PropertyMetadata(
            property,
            property.GetCustomAttribute<DynamoDbPropertyAttribute>()?.Name ?? property.Name,
            getter,
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name,
            primary,
            indexed,
            sortKey ? 1_024 : 2_048);
    }

    private static IReadOnlyDictionary<string, KeySchema> BuildIndexes(
        IReadOnlyCollection<PropertyMetadata> properties)
    {
        var partitionKeys = properties
            .SelectMany(property => property.Property
                .GetCustomAttributes<DynamoDbIndexPartitionKeyAttribute>()
                .Select(attribute => (attribute.IndexName, Property: property)))
            .ToArray();

        var sortKeys = properties
            .SelectMany(property => property.Property
                .GetCustomAttributes<DynamoDbIndexSortKeyAttribute>()
                .Select(attribute => (attribute.IndexName, Property: property)))
            .ToLookup(static pair => pair.IndexName, StringComparer.Ordinal);

        if (partitionKeys.GroupBy(static pair => pair.IndexName, StringComparer.Ordinal).Any(static group => group.Count() != 1) ||
            sortKeys.Any(group => group.Count() > 1 || !partitionKeys.Any(pair => pair.IndexName == group.Key)))
        {
            throw new InvalidOperationException("Each index must have one partition key and at most one sort key.");
        }
        if (partitionKeys.Any(pair => sortKeys[pair.IndexName].Any(sort => sort.Property == pair.Property)))
        {
            throw new InvalidOperationException("Index partition and sort keys must be different properties.");
        }

        return partitionKeys.ToDictionary(
            static pair => pair.IndexName,
            pair => new KeySchema(pair.Property, sortKeys[pair.IndexName].Select(static item => item.Property).SingleOrDefault()),
            StringComparer.Ordinal);
    }
}
