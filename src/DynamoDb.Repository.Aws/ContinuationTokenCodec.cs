using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal static class ContinuationTokenCodec
{
    private const int MaxTokenLength = 16_384;

    public static string? Encode(Dictionary<string, AttributeValue>? key)
    {
        if (key is null || key.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(key.ToDictionary(
            static pair => pair.Key,
            static pair => TokenValue.FromAttributeValue(pair.Value),
            StringComparer.Ordinal));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static Dictionary<string, AttributeValue>? Decode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.Length > MaxTokenLength)
        {
            throw new ArgumentException("Continuation token is too large.", nameof(token));
        }

        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var values = JsonSerializer.Deserialize<Dictionary<string, TokenValue>>(json)
                ?? throw new JsonException("Token payload is empty.");
            if (values.Count == 0)
            {
                throw new JsonException("Token payload has no key attributes.");
            }

            return values.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToAttributeValue(),
                StringComparer.Ordinal);
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or NotSupportedException)
        {
            throw new ArgumentException("Continuation token is invalid.", nameof(token), exception);
        }
    }

    private sealed record TokenValue(
        string? S = null,
        string? N = null,
        bool? Bool = null,
        bool IsNull = false,
        string? B = null,
        IReadOnlyList<string>? Ss = null,
        IReadOnlyList<string>? Ns = null,
        IReadOnlyList<TokenValue>? L = null,
        IReadOnlyDictionary<string, TokenValue>? M = null)
    {
        public static TokenValue FromAttributeValue(AttributeValue value) => new(
            value.S,
            value.N,
            value.BOOL,
            value.NULL == true,
            value.B is null ? null : Convert.ToBase64String(value.B.ToArray()),
            value.SS,
            value.NS,
            value.L?.Select(FromAttributeValue).ToArray(),
            value.M?.ToDictionary(
                static pair => pair.Key,
                static pair => FromAttributeValue(pair.Value),
                StringComparer.Ordinal));

        public AttributeValue ToAttributeValue() => new()
        {
            S = S,
            N = N,
            BOOL = Bool,
            NULL = IsNull,
            B = B is null ? null : new MemoryStream(Convert.FromBase64String(B), writable: false),
            SS = Ss?.ToList(),
            NS = Ns?.ToList(),
            L = L?.Select(static value => value.ToAttributeValue()).ToList(),
            M = M?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToAttributeValue(),
                StringComparer.Ordinal),
        };
    }
}