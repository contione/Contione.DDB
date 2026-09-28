using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DynamoDb.Repository;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class MappingAndTokenTests
{
    [Fact]
    public void Entity_mapper_round_trips_entity_and_uses_attribute_names()
    {
        using var mapper = new EntityMapper<TestEntity>(Mock.Of<IAmazonDynamoDB>());
        var entity = CreateEntity();

        var map = mapper.ToMap(entity);
        var restored = mapper.FromMap(map);

        Assert.Equal(entity.TenantId, restored.TenantId);
        Assert.Equal(entity.Tags, restored.Tags);
        Assert.Contains("display_name", map.Keys);
        Assert.DoesNotContain("Ignored", map.Keys);
    }

    [Fact]
    public void Converter_maps_null_as_dynamodb_null()
    {
        var value = AttributeValueConverter.FromObject(null);

        Assert.True(value.NULL);
    }

    [Fact]
    public void Continuation_token_round_trips_composite_key()
    {
        var key = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new() { S = "tenant-1" },
            ["sk"] = new() { N = "42" },
        };

        var token = ContinuationTokenCodec.Encode(key);
        var restored = ContinuationTokenCodec.Decode(token);

        Assert.NotNull(token);
        Assert.Equal("tenant-1", restored!["pk"].S);
        Assert.Equal("42", restored["sk"].N);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("e30")]
    public void Continuation_token_rejects_invalid_payload(string token)
    {
        Assert.Throws<ArgumentException>(() => ContinuationTokenCodec.Decode(token));
    }

    private static TestEntity CreateEntity() => new()
    {
        TenantId = "tenant-1",
        Id = "account-1",
        Name = "Ada",
        Status = 0,
        CreatedAt = 123,
        Active = true,
        Tags = ["admin", "reader"],
    };
}
