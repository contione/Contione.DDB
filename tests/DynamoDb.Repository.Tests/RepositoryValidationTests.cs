using Amazon.DynamoDBv2;
using DynamoDb.Repository;
using Microsoft.Extensions.Options;
using Moq;

namespace DynamoDb.Repository.Tests;

public sealed class RepositoryValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Page_size_must_be_in_configured_range(int pageSize)
    {
        var repository = CreateRepository();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.Query.ToPageAsync(
                pageSize,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ordering_a_scan_is_rejected()
    {
        var repository = CreateRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.Query
            .Where(entity => entity.Name == "Ada")
            .OrderBy(entity => entity.Id)
            .ToPageAsync(10, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ordering_by_non_sort_key_is_rejected()
    {
        var repository = CreateRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.Query
            .Where(entity => entity.TenantId == "tenant-1")
            .OrderBy(entity => entity.Name)
            .ToPageAsync(10, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Consistent_read_on_secondary_index_is_rejected()
    {
        var repository = CreateRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.Query
            .UseIndex("status-created-index")
            .Where(entity => entity.Status == 0)
            .WithConsistentRead()
            .ToPageAsync(10, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Unknown_index_is_rejected_eagerly()
    {
        var repository = CreateRepository();

        Assert.Throws<InvalidOperationException>(() => repository.Query.UseIndex("missing-index"));
    }

    [Fact]
    public void Take_requires_positive_count()
    {
        var repository = CreateRepository();

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.Query.Take(0));
    }

    private static DynamoDbRepository<TestEntity> CreateRepository() => new(
        Mock.Of<IAmazonDynamoDB>(),
        Options.Create(new DynamoDbRepositoryOptions
        {
            DefaultFetchSize = 25,
            MaxPageSize = 100,
        }));
}
