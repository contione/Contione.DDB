using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository;

internal static class ContinuationTokenCodec
{
    private const int FormatVersion = 1;
    private const int MaxTokenLength = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? Encode(Dictionary<string, AttributeValue>? key, string? context = null)
    {
        if (key is null || key.Count == 0)
        {
            return null;
        }

        var attributes = new Dictionary<string, TokenAttribute>(key.Count, StringComparer.Ordinal);
        foreach (var (name, value) in key)
        {
            if (string.IsNullOrEmpty(name) || !attributes.TryAdd(name, ToTokenAttribute(value, name)))
            {
                throw new ArgumentException("Continuation key attribute names must be non-empty and unique.", nameof(key));
            }
        }

        var payload = new TokenPayload(FormatVersion, context, attributes);
        var token = ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        if (token.Length > MaxTokenLength)
        {
            throw new ArgumentException("Continuation token is too large.", nameof(key));
        }

        return token;
    }

    public static Dictionary<string, AttributeValue>? Decode(string? token, string? context = null)
    {
        if (token is null || token.Length == 0)
        {
            return null;
        }

        if (token.Length > MaxTokenLength)
        {
            throw InvalidToken();
        }

        try
        {
            var json = StrictUtf8.GetString(FromBase64Url(token));
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            var isVersioned = TryReadEnvelope(root, out var envelope);

            JsonElement keyElement;
            if (isVersioned)
            {
                if (envelope.Version != FormatVersion ||
                    !string.Equals(envelope.ContextHash, context, StringComparison.Ordinal))
                {
                    throw new JsonException("Continuation token version or context does not match.");
                }

                keyElement = envelope.Key;
            }
            else
            {
                if (context is not null)
                {
                    throw new JsonException("Continuation token does not contain a query context.");
                }

                keyElement = root;
            }

            return ParseKey(keyElement, allowLegacyAttributes: !isVersioned);
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or DecoderFallbackException or InvalidOperationException)
        {
            throw new ArgumentException("Continuation token is invalid.", nameof(token), exception);
        }
    }

    private static TokenAttribute ToTokenAttribute(AttributeValue? value, string name)
    {
        if (value is null)
        {
            throw new ArgumentException($"Continuation key attribute '{name}' has no value.", "key");
        }

        var typeCount =
            (value.S is null ? 0 : 1) + (value.N is null ? 0 : 1) + (value.B is null ? 0 : 1) +
            (value.BOOL is null ? 0 : 1) + (value.NULL is null ? 0 : 1) + (value.SS is null ? 0 : 1) +
            (value.NS is null ? 0 : 1) + (value.BS is null ? 0 : 1) + (value.L is null ? 0 : 1) +
            (value.M is null ? 0 : 1);

        if (typeCount != 1)
        {
            throw InvalidKeyValue(name);
        }

        if (value.S is not null && value.S.Length > 0)
        {
            return new TokenAttribute(S: value.S);
        }

        if (value.N is not null && IsNumber(value.N))
        {
            return new TokenAttribute(N: value.N);
        }

        if (value.B is not null)
        {
            var bytes = value.B.ToArray();
            if (bytes.Length > 0)
            {
                return new TokenAttribute(B: Convert.ToBase64String(bytes));
            }
        }

        throw InvalidKeyValue(name);
    }

    private static bool TryReadEnvelope(JsonElement root, out ParsedEnvelope envelope)
    {
        envelope = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("v", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new JsonException("Continuation token contains duplicate fields.");
            }
        }

        if (fields.Count != 3 || !fields.TryGetValue("v", out versionElement) ||
            !versionElement.TryGetInt32(out var version) ||
            !fields.TryGetValue("c", out var contextElement) ||
            !fields.TryGetValue("k", out var keyElement))
        {
            throw new JsonException("Continuation token envelope is invalid.");
        }

        var contextHash = contextElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => contextElement.GetString(),
            _ => throw new JsonException("Continuation token context is invalid."),
        };
        envelope = new ParsedEnvelope(version, contextHash, keyElement);
        return true;
    }

    private static Dictionary<string, AttributeValue> ParseKey(JsonElement element, bool allowLegacyAttributes)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Continuation token key is invalid.");
        }

        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Length == 0 || !key.TryAdd(property.Name, ParseAttribute(property.Value, allowLegacyAttributes)))
            {
                throw new JsonException("Continuation token has an invalid key attribute.");
            }
        }

        if (key.Count == 0)
        {
            throw new JsonException("Continuation token key is empty.");
        }

        return key;
    }

    private static AttributeValue ParseAttribute(JsonElement element, bool allowLegacyAttributes)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Continuation key attribute must be an object.");
        }

        string? s = null;
        string? n = null;
        string? b = null;
        var activeTypes = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException("Continuation key attribute contains duplicate fields.");
            }

            switch (property.Name)
            {
                case "S": s = ReadValue(property.Value, allowLegacyAttributes); activeTypes += s is null ? 0 : 1; break;
                case "N": n = ReadValue(property.Value, allowLegacyAttributes); activeTypes += n is null ? 0 : 1; break;
                case "B": b = ReadValue(property.Value, allowLegacyAttributes); activeTypes += b is null ? 0 : 1; break;
                case "Bool" or "Ss" or "Ns" or "L" or "M" when allowLegacyAttributes:
                    if (property.Value.ValueKind != JsonValueKind.Null) throw new JsonException("Unsupported key type.");
                    break;
                case "IsNull" when allowLegacyAttributes:
                    if (property.Value.ValueKind != JsonValueKind.False) throw new JsonException("Null keys are invalid.");
                    break;
                default: throw new JsonException("Unsupported key type.");
            }
        }

        if (activeTypes != 1 || (s is not null && s.Length == 0) || (n is not null && !IsNumber(n)))
        {
            throw new JsonException("Key attribute must contain exactly one non-empty S, N, or B value.");
        }

        if (s is not null) return new AttributeValue { S = s };
        if (n is not null) return new AttributeValue { N = n };
        if (b is not null)
        {
            var bytes = Convert.FromBase64String(b);
            if (bytes.Length == 0 || Convert.ToBase64String(bytes) != b) throw new JsonException("Invalid binary key.");
            return new AttributeValue { B = new MemoryStream(bytes, writable: false) };
        }

        throw new JsonException("Key attribute is empty.");
    }

    private static string? ReadValue(JsonElement element, bool allowNull)
    {
        if (allowNull && element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new JsonException("Key value must be a string.");
        return element.GetString();
    }

    private static bool IsNumber(string value) => value.Length > 0 && value.AsSpan().Trim().Length == value.Length &&
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        if (token.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) || token.Length % 4 == 1)
        {
            throw new FormatException("Invalid base64url token.");
        }

        var base64 = token.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '='));
        if (!string.Equals(ToBase64Url(bytes), token, StringComparison.Ordinal))
        {
            throw new FormatException("Token is not canonical base64url.");
        }

        return bytes;
    }

    private static ArgumentException InvalidKeyValue(string name) =>
        new($"Continuation key attribute '{name}' must contain exactly one non-empty S, N, or B value.", "key");

    private static ArgumentException InvalidToken() =>
        new("Continuation token is invalid or too large.", "token");

    private sealed record TokenAttribute(
        [property: JsonPropertyName("S")] string? S = null,
        [property: JsonPropertyName("N")] string? N = null,
        [property: JsonPropertyName("B")] string? B = null);

    private sealed record TokenPayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("c")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ContextHash,
        [property: JsonPropertyName("k")] IReadOnlyDictionary<string, TokenAttribute> Key);

    private readonly record struct ParsedEnvelope(int Version, string? ContextHash, JsonElement Key);
}
