using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository;

// Adapts query/update constants to the SDK mapping used to store the corresponding property.
internal static class AttributeValueConverter
{
    private static readonly ConcurrentDictionary<Type, Func<IDynamoDBContext, object, DynamoDBEntry>> ValueWriters = new();

    public static AttributeValue FromObject(
        object? value, PropertyMetadata? property = null, DynamoDBEntryConversion? conversion = null,
        IDynamoDBContext? context = null)
    {
        conversion ??= DynamoDBEntryConversion.V2;
        if (value is float single && !float.IsFinite(single) || value is double number && !double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "DynamoDB numbers must be finite.");
        }
        DynamoDBEntry entry;
        if (value is null) entry = DynamoDBNull.Null;
        else if (property?.Converter is { } converter) entry = converter.ToEntry(value);
        else
        {
            if (property?.Mapping is { StoreAsEpochLong: true })
            {
                if (value is not DateTime date) throw new ArgumentException("StoreAsEpoch requires a DateTime property.", nameof(value));
                var seconds = new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds();
                value = seconds;
            }
            try
            {
                entry = conversion.ConvertToEntry(value.GetType(), value);
            }
            catch (InvalidOperationException) when (context is not null)
            {
                // Preserve the declared value type so the SDK handles nested models and collections.
                var write = ValueWriters.GetOrAdd(value.GetType(), static type =>
                    typeof(AttributeValueConverter).GetMethod(nameof(ToEntry), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(type).CreateDelegate<Func<IDynamoDBContext, object, DynamoDBEntry>>());
                entry = write(context, value);
            }
        }
        return new Document { ["value"] = entry }.ToAttributeMap(conversion, isEmptyStringValueEnabled: true)["value"];
    }

    public static AttributeValue FromKey(object? value, PropertyMetadata property, DynamoDBEntryConversion conversion, IDynamoDBContext context)
    {
        var type = Nullable.GetUnderlyingType(property.Property.PropertyType) ?? property.Property.PropertyType;
        if (value is null || !type.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Key '{property.Property.Name}' requires a non-null '{type.Name}' value.");
        }
        var attribute = FromObject(value, property, conversion, context);
        ValidateKey(attribute, property);
        return attribute;
    }

    public static void ValidateKey(AttributeValue? value, PropertyMetadata property)
    {
        var size = value?.S is not null ? Encoding.UTF8.GetByteCount(value.S) : value?.B?.Length;
        if (value is null || size is 0 || size > property.KeySizeLimit || (value.S is null && value.N is null && value.B is null))
        {
            throw new ArgumentException($"Key '{property.Property.Name}' must be a nonempty scalar within the {property.KeySizeLimit}-byte key limit.");
        }
    }

    private static DynamoDBEntry ToEntry<TValue>(IDynamoDBContext context, object value) =>
        context.ToDocument(new Parameter<TValue> { Value = (TValue)value })[nameof(Parameter<TValue>.Value)];

    private sealed class Parameter<TValue>
    {
        public required TValue Value { get; init; }
    }
}
