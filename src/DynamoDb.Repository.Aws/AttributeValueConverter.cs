using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal static class AttributeValueConverter
{
    internal static bool IsKeyType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(char) || type == typeof(byte[]) ||
            type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) ||
            Type.GetTypeCode(type) is TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or
                TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or
                TypeCode.Double or TypeCode.Decimal;
    }

    internal static AttributeValue FromKey(object? value, PropertyMetadata property)
    {
        var type = Nullable.GetUnderlyingType(property.Property.PropertyType) ?? property.Property.PropertyType;
        if (value is null || !type.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Key '{property.Property.Name}' requires a non-null '{type.Name}' value.");
        }
        var result = FromObject(value);
        var size = result.S is not null ? Encoding.UTF8.GetByteCount(result.S) : result.B?.Length;
        if (size is 0 || size > property.KeySizeLimit || (result.S is null && result.N is null && result.B is null))
        {
            throw new ArgumentException($"Key '{property.Property.Name}' must be a nonempty scalar within the {property.KeySizeLimit}-byte key limit.");
        }
        return result;
    }

    public static AttributeValue FromObject(object? value)
    {
        if (value is null)
        {
            return new AttributeValue { NULL = true };
        }

        return value switch
        {
            string text => new AttributeValue { S = text },
            char character => new AttributeValue { S = character.ToString() },
            bool flag => new AttributeValue { BOOL = flag },
            byte[] bytes => new AttributeValue { B = new MemoryStream(bytes, writable: false) },
            Guid guid => new AttributeValue { S = guid.ToString("D") },
            DateTime dateTime => new AttributeValue { S = dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
            DateTimeOffset offset => new AttributeValue { S = offset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
            DateOnly date => new AttributeValue { S = date.ToString("O", CultureInfo.InvariantCulture) },
            TimeOnly time => new AttributeValue { S = time.ToString("O", CultureInfo.InvariantCulture) },
            Enum enumValue => new AttributeValue { S = enumValue.ToString() },
            float number when !float.IsFinite(number) => throw new ArgumentOutOfRangeException(nameof(value), "DynamoDB numbers must be finite."),
            double number when !double.IsFinite(number) => throw new ArgumentOutOfRangeException(nameof(value), "DynamoDB numbers must be finite."),
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
                new AttributeValue { N = Convert.ToString(value, CultureInfo.InvariantCulture) },
            IDictionary dictionary => FromDictionary(dictionary),
            IEnumerable sequence => new AttributeValue
            {
                L = sequence.Cast<object?>().Select(FromObject).ToList(),
            },
            _ => FromJsonElement(JsonSerializer.SerializeToElement(value, value.GetType())),
        };
    }

    public static void WriteJson(Utf8JsonWriter writer, AttributeValue value)
    {
        if (value.NULL == true)
        {
            writer.WriteNullValue();
        }
        else if (value.S is not null)
        {
            writer.WriteStringValue(value.S);
        }
        else if (value.N is not null)
        {
            writer.WriteRawValue(value.N);
        }
        else if (value.B is not null)
        {
            writer.WriteBase64StringValue(value.B.ToArray());
        }
        else if (value.BOOL is not null)
        {
            writer.WriteBooleanValue(value.BOOL.Value);
        }
        else if (value.M is not null)
        {
            writer.WriteStartObject();
            foreach (var pair in value.M)
            {
                writer.WritePropertyName(pair.Key);
                WriteJson(writer, pair.Value);
            }

            writer.WriteEndObject();
        }
        else if (value.L is not null)
        {
            writer.WriteStartArray();
            foreach (var item in value.L)
            {
                WriteJson(writer, item);
            }

            writer.WriteEndArray();
        }
        else if (value.SS is not null)
        {
            WriteStringArray(writer, value.SS);
        }
        else if (value.NS is not null)
        {
            writer.WriteStartArray();
            foreach (var item in value.NS)
            {
                writer.WriteRawValue(item);
            }

            writer.WriteEndArray();
        }
        else if (value.BS is not null)
        {
            writer.WriteStartArray();
            foreach (var item in value.BS) writer.WriteBase64StringValue(item.ToArray());
            writer.WriteEndArray();
        }
        else
        {
            throw new NotSupportedException("The DynamoDB attribute has no supported value type.");
        }
    }

    private static AttributeValue FromDictionary(IDictionary dictionary)
    {
        var map = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                throw new NotSupportedException("DynamoDB map keys must be strings.");
            }

            map[key] = FromObject(entry.Value);
        }

        return new AttributeValue { M = map };
    }

    private static AttributeValue FromJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => new AttributeValue { NULL = true },
        JsonValueKind.String => new AttributeValue { S = element.GetString() },
        JsonValueKind.True => new AttributeValue { BOOL = true },
        JsonValueKind.False => new AttributeValue { BOOL = false },
        JsonValueKind.Number => new AttributeValue { N = element.GetRawText() },
        JsonValueKind.Array => new AttributeValue
        {
            L = element.EnumerateArray().Select(FromJsonElement).ToList(),
        },
        JsonValueKind.Object => new AttributeValue
        {
            M = element.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => FromJsonElement(property.Value),
                StringComparer.Ordinal),
        },
        _ => throw new NotSupportedException($"JSON kind '{element.ValueKind}' is not supported."),
    };

    private static void WriteStringArray(Utf8JsonWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
