using System.Collections;
using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal static class AttributeValueConverter
{
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
        else
        {
            writer.WriteNullValue();
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