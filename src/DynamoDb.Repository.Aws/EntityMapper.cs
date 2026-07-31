using System.Text.Json;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal sealed class EntityMapper<TEntity> where TEntity : class
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly EntityMetadata _metadata = EntityMetadata.For<TEntity>();

    public Dictionary<string, AttributeValue> ToMap(TEntity entity) =>
        _metadata.Properties.ToDictionary(
            static property => property.AttributeName,
            property => AttributeValueConverter.FromObject(property.GetValue(entity)),
            StringComparer.Ordinal);

    public TEntity FromMap(IReadOnlyDictionary<string, AttributeValue> map)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in _metadata.Properties)
            {
                if (!map.TryGetValue(property.AttributeName, out var value))
                {
                    continue;
                }

                writer.WritePropertyName(property.Property.Name);
                AttributeValueConverter.WriteJson(writer, value);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<TEntity>(stream.ToArray(), JsonOptions)
            ?? throw new InvalidOperationException($"Could not materialize '{typeof(TEntity).Name}'.");
    }
}