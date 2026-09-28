using System.Linq.Expressions;
using Accounts.Domain;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class OfficialSdkMappingTests
{
    [Fact]
    public void Repository_reads_official_sdk_documents_and_keeps_native_enum_storage()
    {
        var client = Client();
        using var mapper = new EntityMapper<SdkEntity>(client.Object);
        var original = Entity();
        var sdkMap = mapper.Context.GetTargetTable<SdkEntity>().ToAttributeMap(mapper.Context.ToDocument(original));
        var repositoryMap = mapper.ToMap(original);
        Assert.Equal("1", sdkMap["state"].N);
        Assert.Equal(sdkMap["state"].N, repositoryMap["state"].N);
        Assert.Equal("ADA", repositoryMap["alias"].S);
        Assert.NotNull(repositoryMap["at"].N);
        Assert.Equal("nested", repositoryMap["details"].M["Note"].S);
        var restored = mapper.FromMap(sdkMap);
        Assert.Equal(original.State, restored.State);
        Assert.Equal(original.At, restored.At);
        Assert.Equal("nested", restored.Details.Note);
        client.Verify(x => x.DescribeTableAsync(It.IsAny<DescribeTableRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Query_constants_use_the_same_converter_and_epoch_format_as_sdk_writes()
    {
        var entity = Entity();
        using var mapper = new EntityMapper<SdkEntity>(Client().Object);
        var map = mapper.ToMap(entity);
        Assert.NotNull(map["at"].N);
        var aliases = new[] { "ada", "bob" };
        Expression<Func<SdkEntity, bool>> predicate = x => x.Alias == entity.Alias && x.At == entity.At && x.State == entity.State && aliases.Contains(x.Alias);
        var translated = new ExpressionTranslator(EntityMetadata.For<SdkEntity>(), mapper.Context).Translate(predicate.Body);
        Assert.Contains(translated.Values.Values, value => value.S == map["alias"].S);
        Assert.Contains(translated.Values.Values, value => value.N == map["at"].N);
        Assert.Contains(translated.Values.Values, value => value.S == "BOB");
        Assert.Contains(translated.Values.Values, value => value.N == map["state"].N);
    }

    [Fact]
    public async Task Partial_updates_use_official_converters_and_nested_object_mapping()
    {
        var client = Client();
        UpdateItemRequest? captured = null;
        client.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((request, _) => captured = request).ReturnsAsync(new UpdateItemResponse());
        using var repository = new DynamoDbRepository<SdkEntity>(client.Object, Options.Create(new DynamoDbRepositoryOptions()));
        var date = Entity().At;
        var update = new DynamoDbUpdate<SdkEntity>().Set(x => x.Alias, "bob").Set(x => x.At, date)
            .Set(x => x.Details, new Details { Note = "changed" })
            .Set(x => x.Items, [new Details { Note = "list-item" }]);
        await repository.UpdateAsync("tenant", 1, update, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(captured);
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.S == "BOB");
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.N == new DateTimeOffset(date).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.M is not null && value.M.TryGetValue("Note", out var note) && note.S == "changed");
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.L is { Count: 1 } && value.L[0].M["Note"].S == "list-item");
    }

    [Fact]
    public async Task Local_secondary_index_allows_consistent_reads()
    {
        var client = Client();
        client.Setup(x => x.QueryAsync(It.Is<QueryRequest>(r => r.IndexName == "by-alias" && r.ConsistentRead == true), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse { Count = 1, ScannedCount = 1 });
        using var repository = new DynamoDbRepository<SdkEntity>(client.Object, Options.Create(new DynamoDbRepositoryOptions()));
        Assert.True(await repository.Query.UseIndex("by-alias").Where(x => x.Pk == "tenant").WithConsistentRead().AnyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Account_date_converter_preserves_existing_index_format()
    {
        using var mapper = new EntityMapper<Account>(Client().Object);
        var date = new DateTimeOffset(2026, 9, 28, 10, 30, 0, TimeSpan.FromHours(8));
        var entity = new Account { TenantId = "tenant", AccountId = "id", Name = "Ada", CreatedAt = date };
        var map = mapper.ToMap(entity);
        Assert.Equal(date.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture), map["created_at"].S);
        Assert.Equal(date, mapper.FromMap(map).CreatedAt);
    }

    [Fact]
    public void Disposing_mapping_context_does_not_dispose_the_injected_client()
    {
        var client = Client();
        new EntityMapper<SdkEntity>(client.Object).Dispose();
        client.Verify(x => x.Dispose(), Times.Never);
    }

    private static Mock<IAmazonDynamoDB> Client()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        client.SetupGet(x => x.Config).Returns(new AmazonDynamoDBConfig { RegionEndpoint = Amazon.RegionEndpoint.USEast1 });
        return client;
    }

    private static SdkEntity Entity() => new()
    {
        Pk = "tenant",
        Sk = 1,
        State = SdkState.Active,
        Alias = "ada",
        At = new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Details = new() { Note = "nested" },
    };

    public enum SdkState { Inactive, Active }

    [DynamoDBTable("sdk-entities", LowerCamelCaseProperties = true)]
    public sealed class SdkEntity
    {
        [DynamoDBHashKey("pk")] public string Pk { get; init; } = string.Empty;
        [DynamoDBRangeKey("sk")] public int Sk { get; init; }
        public SdkState State { get; init; }
        [DynamoDBLocalSecondaryIndexRangeKey("by-alias", AttributeName = "alias", Converter = typeof(UpperCaseConverter))]
        public string Alias { get; init; } = string.Empty;
        [DynamoDBProperty("at", StoreAsEpochLong = true)] public DateTime At { get; init; }
        public Details Details { get; init; } = new();
        public List<Details> Items { get; init; } = [];
    }

    public sealed class Details
    {
        public string Note { get; init; } = string.Empty;
    }

    public sealed class UpperCaseConverter : IPropertyConverter
    {
        public DynamoDBEntry ToEntry(object value) => ((string)value).ToUpperInvariant();
        public object FromEntry(DynamoDBEntry entry) => entry.AsString();
    }
}
