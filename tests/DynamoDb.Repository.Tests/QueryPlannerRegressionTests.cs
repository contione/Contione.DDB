using System.Linq.Expressions;
using Amazon.DynamoDBv2.DataModel;
using DynamoDb.Repository;

namespace DynamoDb.Repository.Tests;

public sealed class QueryPlannerRegressionTests
{
    [Fact]
    public void Planner_combines_inclusive_sort_key_bounds_into_between()
    {
        Expression<Func<NumericSortEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Sequence >= 100 && entity.Sequence <= 200;

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.True(plan.IsQuery);
        Assert.Null(plan.FilterExpression);
        Assert.Contains("BETWEEN", plan.KeyExpression, StringComparison.Ordinal);
        Assert.Equal(3, plan.Values.Count);
    }

    [Fact]
    public void Planner_rejects_strict_two_sided_sort_key_bounds()
    {
        Expression<Func<NumericSortEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Sequence > 100 && entity.Sequence < 200;

        var exception = Assert.Throws<NotSupportedException>(
            () => QueryPlanner.Create([predicate], indexName: null));

        Assert.Contains("one sort key condition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_rejects_contradictory_sort_key_bounds()
    {
        Expression<Func<NumericSortEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Sequence >= 200 && entity.Sequence <= 100;

        var exception = Assert.Throws<NotSupportedException>(
            () => QueryPlanner.Create([predicate], indexName: null));

        Assert.Contains("lower bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_deduplicates_equal_partition_key_conditions()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.TenantId == "tenant-1";

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.True(plan.IsQuery);
        Assert.Single(plan.Values);
    }

    [Fact]
    public void Planner_rejects_conflicting_partition_key_conditions()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.TenantId == "tenant-2";

        var exception = Assert.Throws<NotSupportedException>(
            () => QueryPlanner.Create([predicate], indexName: null));

        Assert.Contains("conflicting partition key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_does_not_treat_captured_entity_property_as_partition_key()
    {
        var captured = new TestEntity
        {
            TenantId = "tenant-1",
            Id = "100",
            Name = "Ada",
        };
        Expression<Func<TestEntity, bool>> predicate = entity => entity.Name == captured.TenantId;

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.False(plan.IsQuery);
        Assert.Null(plan.KeyExpression);
        Assert.Contains("display_name", plan.Names.Values);
        Assert.DoesNotContain("pk", plan.Names.Values);
    }

    [Fact]
    public void Planner_scans_partition_key_condition_without_partition_equality()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => entity.TenantId.StartsWith("tenant");

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.False(plan.IsQuery);
        Assert.Null(plan.KeyExpression);
        Assert.NotNull(plan.FilterExpression);
        Assert.Contains("pk", plan.Names.Values);
    }

    [Fact]
    public void Planner_scans_partition_and_sort_key_filters_without_partition_equality()
    {
        Expression<Func<NumericSortEntity, bool>> predicate = entity =>
            entity.TenantId.StartsWith("tenant") && entity.Sequence >= 100;

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.False(plan.IsQuery);
        Assert.Null(plan.KeyExpression);
        Assert.NotNull(plan.FilterExpression);
        Assert.Contains("TenantId", plan.Names.Values);
        Assert.Contains("Sequence", plan.Names.Values);
    }

    [Fact]
    public void Planner_rejects_sort_key_filter_even_when_partition_key_is_present()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Id.Contains("00");

        Assert.Throws<NotSupportedException>(() => QueryPlanner.Create([predicate], indexName: null));
    }

    [Fact]
    public void Planner_rejects_sort_key_not_equal_for_query()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Id != "100";

        Assert.Throws<NotSupportedException>(() => QueryPlanner.Create([predicate], indexName: null));
    }

    [Fact]
    public void Planner_rejects_numeric_range_on_enum_sort_key()
    {
        Expression<Func<EnumSortEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && (int)entity.Status >= 0 && (int)entity.Status <= 1;

        var exception = Assert.Throws<NotSupportedException>(
            () => QueryPlanner.Create([predicate], indexName: null));

        Assert.Contains("Ordering an enum", exception.Message, StringComparison.Ordinal);
    }

    [DynamoDBTable("numeric-sort-entities")]
    private sealed class NumericSortEntity
    {
        [DynamoDBHashKey]
        public string TenantId { get; init; } = string.Empty;

        [DynamoDBRangeKey]
        public int Sequence { get; init; }
    }

    [DynamoDBTable("enum-sort-entities")]
    private sealed class EnumSortEntity
    {
        [DynamoDBHashKey]
        public string TenantId { get; init; } = string.Empty;

        [DynamoDBRangeKey]
        public SortStatus Status { get; init; }
    }

    private enum SortStatus
    {
        Pending,
        Active,
    }
}
