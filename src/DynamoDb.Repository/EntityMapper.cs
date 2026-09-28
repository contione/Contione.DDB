using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository;

internal sealed class EntityMapper<TEntity> : IDisposable where TEntity : class
{
    private readonly EntityMetadata _metadata = EntityMetadata.For<TEntity>();

    public EntityMapper(IAmazonDynamoDB client)
    {
        Context = new DynamoDBContext(client, new DynamoDBContextConfig
        {
            DisableFetchingTableMetadata = true,
            Conversion = _metadata.Conversion,
            IsEmptyStringValueEnabled = true,
            RetrieveDateTimeInUtc = true,
        });
    }

    public IDynamoDBContext Context { get; }

    public Dictionary<string, AttributeValue> ToMap(TEntity entity)
    {
        var map = Context.ToDocument(entity).ToAttributeMap(_metadata.Conversion, isEmptyStringValueEnabled: true);
        foreach (var property in _metadata.Properties.Where(static property => property.IsPrimaryKey || property.IsIndexKey))
        {
            map.TryGetValue(property.AttributeName, out var value);
            if (!property.IsPrimaryKey && (value is null || value.NULL == true))
            {
                map.Remove(property.AttributeName);
                continue;
            }
            AttributeValueConverter.ValidateKey(value, property);
        }
        return map;
    }

    public TEntity FromMap(Dictionary<string, AttributeValue> map) =>
        Context.FromDocument<TEntity>(Document.FromAttributeMap(map));

    public void Dispose() => Context.Dispose();
}
