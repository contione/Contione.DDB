using System.Collections.Concurrent;
using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;

namespace DynamoDb.Repository;

// Query planning only: entity serialization belongs to DynamoDBContext.
internal sealed record PropertyMetadata(
    PropertyInfo Property,
    string AttributeName,
    DynamoDBPropertyAttribute? Mapping,
    IPropertyConverter? Converter,
    bool IsPrimaryKey,
    bool IsIndexKey,
    int KeySizeLimit)
{
    public object? GetValue(object entity) => Property.GetValue(entity);
}

internal sealed record KeySchema(PropertyMetadata PartitionKey, PropertyMetadata? SortKey, bool IsGlobalIndex = false);

internal sealed class EntityMetadata
{
    private static readonly ConcurrentDictionary<Type, EntityMetadata> Cache = new();
    private readonly IReadOnlyDictionary<string, PropertyMetadata> _propertiesByClrName;
    private readonly Dictionary<string, KeySchema> _indexes = new(StringComparer.Ordinal);

    private EntityMetadata(Type entityType)
    {
        var table = entityType.GetCustomAttribute<DynamoDBTableAttribute>()
            ?? throw new InvalidOperationException($"Entity '{entityType.Name}' must declare DynamoDBTableAttribute.");
        TableName = table.TableName;
        Conversion = table.Conversion == ConversionSchema.V1 ? DynamoDBEntryConversion.V1 : DynamoDBEntryConversion.V2;
        Properties = entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetMethod is { IsPublic: true } &&
                property.GetIndexParameters().Length == 0 && !property.IsDefined(typeof(DynamoDBIgnoreAttribute)))
            .Select(property => CreateProperty(property, table.LowerCamelCaseProperties)).ToArray();
        if (Properties.Select(static property => property.AttributeName).Distinct(StringComparer.Ordinal).Count() != Properties.Count)
        {
            throw new InvalidOperationException($"Entity '{entityType.Name}' maps multiple properties to the same attribute.");
        }
        _propertiesByClrName = Properties.ToDictionary(static property => property.Property.Name, StringComparer.Ordinal);
        var partitionKeys = Properties.Where(static property => HasExactAttribute<DynamoDBHashKeyAttribute>(property.Property)).ToArray();
        var sortKeys = Properties.Where(static property => HasExactAttribute<DynamoDBRangeKeyAttribute>(property.Property)).ToArray();
        if (partitionKeys.Length != 1 || sortKeys.Length > 1 || partitionKeys[0] == sortKeys.SingleOrDefault())
        {
            throw new InvalidOperationException("An entity must declare one hash key and at most one distinct range key.");
        }
        PrimaryKey = new(partitionKeys[0], sortKeys.SingleOrDefault());
        BuildIndexes();
    }

    public string TableName { get; }
    public DynamoDBEntryConversion Conversion { get; }
    public IReadOnlyList<PropertyMetadata> Properties { get; }
    public KeySchema PrimaryKey { get; }

    public static EntityMetadata For<TEntity>() where TEntity : class =>
        Cache.GetOrAdd(typeof(TEntity), static type => new EntityMetadata(type));

    public PropertyMetadata GetProperty(MemberInfo member) =>
        _propertiesByClrName.TryGetValue(member.Name, out var property) && property.Property == member
            ? property : throw new NotSupportedException($"Property '{member.Name}' is not mapped to DynamoDB.");

    public KeySchema GetKeySchema(string? indexName) => indexName is null ? PrimaryKey :
        _indexes.TryGetValue(indexName, out var schema) ? schema :
        throw new InvalidOperationException($"Index '{indexName}' is not declared on the entity.");

    private static PropertyMetadata CreateProperty(PropertyInfo property, bool lowerCamelCase)
    {
        var mappings = property.GetCustomAttributes<DynamoDBPropertyAttribute>().ToArray();
        var attributeName = mappings.Select(static mapping => mapping.AttributeName)
            .Where(static name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal).SingleOrDefault()
            ?? (lowerCamelCase ? char.ToLowerInvariant(property.Name[0]) + property.Name[1..] : property.Name);
        // Inspect the deprecated option only to reject its post-2038 format fallback explicitly.
#pragma warning disable CS0618
        if (mappings.Any(static attribute => attribute.StoreAsEpoch))
#pragma warning restore CS0618
        {
            throw new NotSupportedException("Use the SDK StoreAsEpochLong option instead of the deprecated StoreAsEpoch option.");
        }
        var mapping = mappings.FirstOrDefault(static attribute => attribute.Converter is not null || attribute.StoreAsEpochLong);
        var converter = mapping?.Converter is { } converterType
            ? Activator.CreateInstance(converterType) as IPropertyConverter
                ?? throw new InvalidOperationException($"Converter for '{property.Name}' must implement IPropertyConverter.")
            : null;
        if (property.IsDefined(typeof(DynamoDBVersionAttribute)))
        {
            throw new NotSupportedException("Automatic DynamoDBVersion handling requires DynamoDBContext.SaveAsync. Use an explicit write condition with this repository.");
        }
        var primary = HasExactAttribute<DynamoDBHashKeyAttribute>(property) || HasExactAttribute<DynamoDBRangeKeyAttribute>(property);
        var indexed = property.IsDefined(typeof(DynamoDBGlobalSecondaryIndexHashKeyAttribute)) ||
            property.IsDefined(typeof(DynamoDBGlobalSecondaryIndexRangeKeyAttribute)) || property.IsDefined(typeof(DynamoDBLocalSecondaryIndexRangeKeyAttribute));
        var sort = property.IsDefined(typeof(DynamoDBRangeKeyAttribute)) || property.IsDefined(typeof(DynamoDBGlobalSecondaryIndexRangeKeyAttribute)) ||
            property.IsDefined(typeof(DynamoDBLocalSecondaryIndexRangeKeyAttribute));
        return new(property, attributeName, mapping, converter, primary, indexed, sort ? 1_024 : 2_048);
    }

    private void BuildIndexes()
    {
        var partitions = Properties.SelectMany(property => property.Property.GetCustomAttributes<DynamoDBGlobalSecondaryIndexHashKeyAttribute>()
            .SelectMany(attribute => attribute.IndexNames.Select(name => (Name: name, Property: property)))).ToArray();
        var ranges = Properties.SelectMany(property => property.Property.GetCustomAttributes<DynamoDBGlobalSecondaryIndexRangeKeyAttribute>()
            .SelectMany(attribute => attribute.IndexNames.Select(name => (Name: name, Property: property)))).ToArray();
        foreach (var group in partitions.GroupBy(static item => item.Name, StringComparer.Ordinal))
        {
            var partition = group.Single().Property;
            var range = ranges.Where(item => item.Name == group.Key).Select(static item => item.Property).SingleOrDefault();
            if (partition == range) throw new InvalidOperationException("Index hash and range keys must be distinct.");
            _indexes.Add(group.Key, new(partition, range, IsGlobalIndex: true));
        }
        if (ranges.Any(item => !_indexes.ContainsKey(item.Name)))
        {
            throw new InvalidOperationException("A global secondary index range key requires a declared hash key.");
        }
        foreach (var property in Properties)
        {
            foreach (var index in property.Property.GetCustomAttributes<DynamoDBLocalSecondaryIndexRangeKeyAttribute>().SelectMany(static attribute => attribute.IndexNames))
            {
                if (PrimaryKey.SortKey is null || property == PrimaryKey.PartitionKey)
                {
                    throw new InvalidOperationException("A local secondary index requires a table range key and a distinct index range key.");
                }
                _indexes.Add(index, new(PrimaryKey.PartitionKey, property));
            }
        }
    }

    // SDK secondary key attributes inherit from primary key attributes.
    private static bool HasExactAttribute<TAttribute>(PropertyInfo property) where TAttribute : Attribute =>
        property.GetCustomAttributes<TAttribute>().Any(static attribute => attribute.GetType() == typeof(TAttribute));
}
