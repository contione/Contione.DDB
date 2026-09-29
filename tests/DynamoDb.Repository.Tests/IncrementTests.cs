using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class IncrementTests
{
    [Fact]
    public async Task Increment_uses_mapped_numeric_attributes_and_combines_with_set_remove_and_condition()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        UpdateItemRequest? captured = null;
        var cancellation = TestContext.Current.CancellationToken;
        client.Setup(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), cancellation))
            .Callback<UpdateItemRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new UpdateItemResponse());
        using var repository = Create(client);
        var update = new DynamoDbUpdate<NumericEntity>()
            .Increment(x => x.Amount, -1.25m)
            .Increment(x => x.OptionalCount, 2L)
            .Set(x => x.Label, "updated")
            .Remove(x => x.Obsolete);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await repository.UpdateAsync("counter", 1, update, x => x.Amount >= 5m, cancellation);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.NotNull(captured);
        Assert.Equal("SET #u1 = if_not_exists(#u1, :incrementZero) + :u1, #u2 = if_not_exists(#u2, :incrementZero) + :u2, #u3 = :u3 REMOVE #u4", captured.UpdateExpression);
        Assert.Equal("amount", captured.ExpressionAttributeNames["#u1"]);
        Assert.Equal("OptionalCount", captured.ExpressionAttributeNames["#u2"]);
        Assert.Equal("-1.25", captured.ExpressionAttributeValues[":u1"].N);
        Assert.Equal("2", captured.ExpressionAttributeValues[":u2"].N);
        Assert.Equal("0", captured.ExpressionAttributeValues[":incrementZero"].N);
        Assert.Equal("updated", captured.ExpressionAttributeValues[":u3"].S);
        Assert.Contains(captured.ExpressionAttributeValues.Values, value => value.N == "5");
        Assert.StartsWith("attribute_exists(#existing) AND (", captured.ConditionExpression, StringComparison.Ordinal);
        Assert.Equal("Pk", captured.ExpressionAttributeNames["#existing"]);
        client.Verify(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), cancellation), Times.Once);
    }

    [Fact]
    public async Task Increment_rejects_keys_duplicates_conversions_and_custom_numeric_formats_before_sending()
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        using var repository = Create(client);
        var updates = new[]
        {
            new DynamoDbUpdate<NumericEntity>().Increment(x => x.Sk, 1),
            new DynamoDbUpdate<NumericEntity>().Increment(x => x.Amount, 1m).Set(x => x.Amount, 2m),
            new DynamoDbUpdate<NumericEntity>().Increment(x => x.Amount, 1m).Remove(x => x.Amount),
            new DynamoDbUpdate<NumericEntity>().Increment(x => (double)x.Amount, 1d),
            new DynamoDbUpdate<NumericEntity>().Increment(x => x.Converted, 1L),
            new DynamoDbUpdate<NumericEntity>().Increment(x => x.Unsupported, (Half)1),
        };
        foreach (var update in updates)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => repository.UpdateAsync(
                "counter", 1, update, cancellationToken: TestContext.Current.CancellationToken));
        }
        client.Verify(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Increment_rejects_nonfinite_numbers(double amount)
    {
        var client = new Mock<IAmazonDynamoDB>(MockBehavior.Strict);
        using var repository = Create(client);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpdateAsync(
            "counter", 1, new DynamoDbUpdate<NumericEntity>().Increment(x => x.Ratio, amount),
            cancellationToken: TestContext.Current.CancellationToken));
        client.Verify(x => x.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Increment_preserves_immutable_builder_and_rejects_null_selectors()
    {
        var original = new DynamoDbUpdate<NumericEntity>();
        var first = original.Increment(x => x.Amount, 1m);
        var second = first.Increment(x => x.OptionalCount, -1L);
        Assert.Empty(original.Changes);
        Assert.Single(first.Changes);
        Assert.Equal(2, second.Changes.Count);
        Assert.Throws<ArgumentNullException>(() => original.Increment<decimal>(null!, 1m));
    }

    private static DynamoDbRepository<NumericEntity> Create(Mock<IAmazonDynamoDB> client)
    {
        client.SetupGet(x => x.Config).Returns(new AmazonDynamoDBConfig());
        return new(client.Object, Options.Create(new DynamoDbRepositoryOptions()));
    }

    [DynamoDBTable("counters")]
    public sealed class NumericEntity
    {
        [DynamoDBHashKey]
        public string Pk { get; init; } = string.Empty;
        [DynamoDBRangeKey]
        public int Sk { get; init; }
        [DynamoDBProperty("amount")]
        public decimal Amount { get; init; }
        public long? OptionalCount { get; init; }
        public double Ratio { get; init; }
        public string? Label { get; init; }
        public string? Obsolete { get; init; }
        public Half Unsupported { get; init; }
        [DynamoDBProperty(typeof(OffsetConverter))]
        public long Converted { get; init; }
    }

    public sealed class OffsetConverter : IPropertyConverter
    {
        public DynamoDBEntry ToEntry(object value) => new Primitive(((long)value + 100).ToString(CultureInfo.InvariantCulture), true);
        public object FromEntry(DynamoDBEntry entry) => entry.AsLong() - 100;
    }
}
