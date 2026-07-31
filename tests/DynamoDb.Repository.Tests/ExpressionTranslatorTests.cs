using System.Linq.Expressions;
using DynamoDb.Repository.Aws;

namespace DynamoDb.Repository.Tests;

public sealed class ExpressionTranslatorTests
{
    [Fact]
    public void Planner_uses_query_for_partition_key_equality()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Name == "Ada";

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.True(plan.IsQuery);
        Assert.NotNull(plan.KeyExpression);
        Assert.NotNull(plan.FilterExpression);
        Assert.Contains("pk", plan.Names.Values);
        Assert.Contains("display_name", plan.Names.Values);
    }

    [Fact]
    public void Planner_uses_scan_without_partition_key()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => entity.Name == "Ada";

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.False(plan.IsQuery);
        Assert.Null(plan.KeyExpression);
        Assert.NotNull(plan.FilterExpression);
    }

    [Fact]
    public void Planner_places_sort_key_prefix_in_key_expression()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.TenantId == "tenant-1" && entity.Id.StartsWith("100");

        var plan = QueryPlanner.Create([predicate], indexName: null);

        Assert.True(plan.IsQuery);
        Assert.Null(plan.FilterExpression);
        Assert.Contains("begins_with", plan.KeyExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_uses_declared_secondary_index()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.Status == 0 && entity.CreatedAt > 100;

        var plan = QueryPlanner.Create([predicate], "status-created-index");

        Assert.True(plan.IsQuery);
        Assert.Null(plan.FilterExpression);
        Assert.Contains("status", plan.Names.Values);
        Assert.Contains("created_at", plan.Names.Values);
    }

    [Fact]
    public void Translator_supports_string_functions_and_boolean_members()
    {
        Expression<Func<TestEntity, bool>> predicate = entity =>
            entity.Name.StartsWith("A") && entity.Active;
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Contains("begins_with", result.Expression, StringComparison.Ordinal);
        Assert.Contains("AND", result.Expression, StringComparison.Ordinal);
        Assert.Contains(result.Values.Values, value => value.BOOL == true);
    }

    [Fact]
    public void Translator_supports_in_expression()
    {
        var statuses = new[] { 0, 1, 3 };
        Expression<Func<TestEntity, bool>> predicate = entity => statuses.Contains(entity.Status);
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Contains(" IN ", result.Expression, StringComparison.Ordinal);
        Assert.Equal(3, result.Values.Count);
    }

    [Fact]
    public void Translator_supports_collection_contains()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => entity.Tags.Contains("admin");
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Contains("contains", result.Expression, StringComparison.Ordinal);
    }

    [Fact]
    public void Translator_rejects_untranslatable_method()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => entity.Name.ToLower() == "ada";
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        Assert.Throws<NotSupportedException>(() => translator.Translate(predicate.Body));
    }
}