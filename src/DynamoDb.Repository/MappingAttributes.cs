namespace DynamoDb.Repository;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DynamoDbTableAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Table name is required.", nameof(name))
        : name;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class DynamoDbPropertyAttribute(string name) : Attribute
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Attribute name is required.", nameof(name))
        : name;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class DynamoDbPartitionKeyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class DynamoDbSortKeyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class DynamoDbIndexPartitionKeyAttribute(string indexName) : Attribute
{
    public string IndexName { get; } = string.IsNullOrWhiteSpace(indexName)
        ? throw new ArgumentException("Index name is required.", nameof(indexName))
        : indexName;
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true, Inherited = true)]
public sealed class DynamoDbIndexSortKeyAttribute(string indexName) : Attribute
{
    public string IndexName { get; } = string.IsNullOrWhiteSpace(indexName)
        ? throw new ArgumentException("Index name is required.", nameof(indexName))
        : indexName;
}

[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class DynamoDbIgnoreAttribute : Attribute;