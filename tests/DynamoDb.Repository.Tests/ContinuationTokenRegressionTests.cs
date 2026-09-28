using System.Text;
using Amazon.DynamoDBv2.Model;
using DynamoDb.Repository.Aws;

namespace DynamoDb.Repository.Tests;

public sealed class ContinuationTokenRegressionTests
{
    [Fact]
    public void Context_bound_token_round_trips_only_for_the_same_context()
    {
        var key = CreateCompositeKey();
        var token = ContinuationTokenCodec.Encode(key, "accounts|by-tenant|ascending");

        var restored = ContinuationTokenCodec.Decode(token, "accounts|by-tenant|ascending");

        Assert.Equal("tenant-1", restored!["pk"].S);
        Assert.Equal("42", restored["sk"].N);
        Assert.Throws<ArgumentException>(() =>
            ContinuationTokenCodec.Decode(token, "accounts|by-tenant|descending"));
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Decode(token));
    }

    [Fact]
    public void Decode_accepts_previous_unversioned_token_without_context()
    {
        const string legacyJson =
            "{\"pk\":{\"S\":\"tenant-1\",\"N\":null,\"Bool\":null,\"IsNull\":false,\"B\":null,\"Ss\":null,\"Ns\":null,\"L\":null,\"M\":null}}";
        var token = ToBase64Url(legacyJson);

        var restored = ContinuationTokenCodec.Decode(token);

        Assert.Equal("tenant-1", restored!["pk"].S);
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Decode(token, "accounts|by-tenant"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pk\":null}")]
    [InlineData("{\"pk\":{}}")]
    [InlineData("{\"\":{\"S\":\"tenant-1\"}}")]
    [InlineData("{\"pk\":{\"S\":\"\"}}")]
    [InlineData("{\"pk\":{\"S\":\"tenant-1\",\"N\":\"1\"}}")]
    [InlineData("{\"pk\":{\"BOOL\":true}}")]
    [InlineData("{\"pk\":{\"B\":\"not-base64\"}}")]
    [InlineData("{\"pk\":{\"N\":\"NaN\"}}")]
    [InlineData("{\"pk\":{\"N\":\"not-a-number\"}}")]
    public void Decode_rejects_empty_malformed_or_non_key_attributes(string json)
    {
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Decode(ToBase64Url(json)));
    }

    [Fact]
    public void Encode_rejects_unsupported_or_multiple_attribute_types()
    {
        var unsupported = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { BOOL = true },
        };
        var multipleTypes = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { S = "tenant-1", N = "1" },
        };
        var emptyString = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { S = string.Empty },
        };

        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Encode(unsupported));
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Encode(multipleTypes));
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Encode(emptyString));
    }

    [Fact]
    public void Encode_and_decode_support_maximum_composite_key_with_json_escapes()
    {
        var key = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { S = new string('\u0001', 2_048) },
            ["sk"] = new() { S = new string('\u0001', 1_024) },
            ["gsi_pk"] = new() { S = new string('\u0001', 2_048) },
            ["gsi_sk"] = new() { S = new string('\u0001', 1_024) },
        };

        var token = ContinuationTokenCodec.Encode(key, "table|index|ascending");
        var restored = ContinuationTokenCodec.Decode(token, "table|index|ascending");

        Assert.NotNull(token);
        Assert.Equal(new string('\u0001', 2_048), restored!["pk"].S);
        Assert.Equal(new string('\u0001', 1_024), restored["sk"].S);
    }

    [Fact]
    public void Encode_and_decode_round_trip_binary_key()
    {
        byte[] expected = [0x00, 0x7F, 0xFF];
        var key = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { B = new MemoryStream(expected, writable: false) },
        };

        var restored = ContinuationTokenCodec.Decode(ContinuationTokenCodec.Encode(key));

        Assert.Equal(expected, restored!["pk"].B.ToArray());
    }

    [Fact]
    public void Encode_and_decode_apply_the_same_token_size_limit()
    {
        var oversizedKey = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { S = new string('x', 50_000) },
        };

        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Encode(oversizedKey));
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Decode(new string('A', 65_537)));
    }

    private static Dictionary<string, AttributeValue> CreateCompositeKey() => new()
    {
        ["pk"] = new() { S = "tenant-1" },
        ["sk"] = new() { N = "42" },
    };

    private static string ToBase64Url(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
