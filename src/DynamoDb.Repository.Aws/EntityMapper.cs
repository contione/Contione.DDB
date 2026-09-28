using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal sealed class EntityMapper<TEntity> where TEntity : class
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly EntityMetadata _metadata = EntityMetadata.For<TEntity>();

    public Dictionary<string, AttributeValue> ToMap(TEntity entity)
    {
        var map = new Dictionary<string, AttributeValue>(_metadata.Properties.Count, StringComparer.Ordinal);
        foreach (var property in _metadata.Properties)
        {
            var value = property.GetValue(entity);
            // Missing secondary keys intentionally exclude the item from a sparse index.
            if (value is null && property.IsIndexKey && !property.IsPrimaryKey) continue;
            map[property.AttributeName] = property.IsPrimaryKey || property.IsIndexKey
                ? AttributeValueConverter.FromKey(value, property)
                : AttributeValueConverter.FromObject(value);
        }
        return map;
    }

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

                writer.WritePropertyName(property.JsonName);
                AttributeValueConverter.WriteJson(writer, value);
            }

            writer.WriteEndObject();
        }

        stream.Position = 0;
        return JsonSerializer.Deserialize<TEntity>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Could not materialize '{typeof(TEntity).Name}'.");
    }
}
