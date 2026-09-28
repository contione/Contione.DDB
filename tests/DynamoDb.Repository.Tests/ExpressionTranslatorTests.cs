using System.Linq.Expressions;
using Amazon.DynamoDBv2.DataModel;
using DynamoDb.Repository;

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

    [Fact]
    public void Translator_stores_enum_comparison_values_as_names()
    {
        Expression<Func<EnumEntity, bool>> predicate = entity => entity.Status == AccountStatus.Active;
        var translator = new ExpressionTranslator(EntityMetadata.For<EnumEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Equal("Active", Assert.Single(result.Values).Value.S);
    }

    [Fact]
    public void Translator_stores_char_comparison_values_as_strings()
    {
        Expression<Func<EnumEntity, bool>> predicate = entity => entity.Grade == 'A';
        var translator = new ExpressionTranslator(EntityMetadata.For<EnumEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Equal("A", Assert.Single(result.Values).Value.S);
    }

    [Fact]
    public void Translator_normalizes_char_numeric_comparison_to_stored_character()
    {
        Expression<Func<EnumEntity, bool>> predicate = entity => (int)entity.Grade == 65;
        var translator = new ExpressionTranslator(EntityMetadata.For<EnumEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Equal("A", Assert.Single(result.Values).Value.S);
    }

    [Fact]
    public void Translator_supports_exact_numeric_widening()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => entity.Status == 3L;
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Equal("3", Assert.Single(result.Values).Value.N);
    }

    [Fact]
    public void Translator_rejects_narrowing_property_conversion()
    {
        Expression<Func<TestEntity, bool>> predicate = entity => (short)entity.Status == 3;
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        Assert.Throws<NotSupportedException>(() => translator.Translate(predicate.Body));
    }

    [Fact]
    public void Translator_normalizes_enum_numeric_equality_to_stored_name()
    {
        Expression<Func<EnumEntity, bool>> predicate = entity => (int)entity.Status == 1;
        var translator = new ExpressionTranslator(EntityMetadata.For<EnumEntity>());

        var result = translator.Translate(predicate.Body);

        Assert.Equal("Active", Assert.Single(result.Values).Value.S);
    }

    [Fact]
    public void Translator_rejects_enum_numeric_ordering_that_changes_storage_semantics()
    {
        Expression<Func<EnumEntity, bool>> predicate = entity => (int)entity.Status < 2;
        var translator = new ExpressionTranslator(EntityMetadata.For<EnumEntity>());

        Assert.Throws<NotSupportedException>(() => translator.Translate(predicate.Body));
    }

    [Fact]
    public void Translator_rejects_in_expression_with_more_than_100_values()
    {
        var statuses = Enumerable.Range(0, 101).ToArray();
        Expression<Func<TestEntity, bool>> predicate = entity => statuses.Contains(entity.Status);
        var translator = new ExpressionTranslator(EntityMetadata.For<TestEntity>());

        var exception = Assert.Throws<NotSupportedException>(() => translator.Translate(predicate.Body));

        Assert.Contains("at most 100", exception.Message, StringComparison.Ordinal);
    }

    [DynamoDBTable("enum-tests")]
    private sealed class EnumEntity
    {
        [DynamoDBHashKey]
        public string TenantId { get; init; } = string.Empty;

        [DynamoDBProperty(typeof(EnumNameConverter<AccountStatus>))]
        public AccountStatus Status { get; init; }

        public char Grade { get; init; }
    }

    private enum AccountStatus
    {
        Pending,
        Active,
    }
}
