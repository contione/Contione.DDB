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
        Context = new DynamoDBContextBuilder().WithDynamoDBClient(() => client)
            .ConfigureContext(config =>
            {
                config.DisableFetchingTableMetadata = true;
                config.Conversion = _metadata.Conversion;
                config.IsEmptyStringValueEnabled = true;
                config.RetrieveDateTimeInUtc = true;
            }).Build();
    }

    public IDynamoDBContext Context { get; }

    public Dictionary<string, AttributeValue> ToMap(TEntity entity)
    {
        var map = Context.GetTargetTable<TEntity>().ToAttributeMap(Context.ToDocument(entity));
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
        Context.FromDocument<TEntity>(Context.GetTargetTable<TEntity>().FromAttributeMap(map));

    public void Dispose() => Context.Dispose();
}
