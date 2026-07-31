using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DynamoDb.Repository.Aws;

internal sealed record PropertyMetadata(
    PropertyInfo Property,
    string AttributeName,
    Func<object, object?> GetValue);

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
            .Where(static property => property.GetMethod is not null &&
                property.GetCustomAttribute<DynamoDbIgnoreAttribute>() is null)
            .Select(CreateProperty)
            .ToArray();

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
        var instance = Expression.Parameter(typeof(object), "instance");
        var getter = Expression.Lambda<Func<object, object?>>(
            Expression.Convert(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                typeof(object)),
            instance).Compile();

        return new PropertyMetadata(
            property,
            property.GetCustomAttribute<DynamoDbPropertyAttribute>()?.Name ?? property.Name,
            getter);
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

        return partitionKeys.ToDictionary(
            static pair => pair.IndexName,
            pair => new KeySchema(pair.Property, sortKeys[pair.IndexName].Select(static item => item.Property).SingleOrDefault()),
            StringComparer.Ordinal);
    }
}