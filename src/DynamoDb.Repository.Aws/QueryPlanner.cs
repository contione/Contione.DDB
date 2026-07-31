using System.Linq.Expressions;

namespace DynamoDb.Repository.Aws;

internal sealed record QueryPlan(
    bool IsQuery,
    string? KeyExpression,
    string? FilterExpression,
    Dictionary<string, string> Names,
    Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue> Values);

internal static class QueryPlanner
{
    public static QueryPlan Create<TEntity>(
        IReadOnlyList<Expression<Func<TEntity, bool>>> predicates,
        string? indexName) where TEntity : class
    {
        var metadata = EntityMetadata.For<TEntity>();
        var schema = metadata.GetKeySchema(indexName);
        var terms = predicates.SelectMany(static predicate => FlattenAnd(predicate.Body)).ToList();

        var partitionTerm = terms.FirstOrDefault(term =>
            IsComparisonOn(term, schema.PartitionKey, ExpressionType.Equal));
        var isQuery = partitionTerm is not null;
        var keyTerms = new List<Expression>();

        if (partitionTerm is not null)
        {
            keyTerms.Add(partitionTerm);
            terms.Remove(partitionTerm);

            if (schema.SortKey is not null)
            {
                var sortTerm = terms.FirstOrDefault(term => IsSortCondition(term, schema.SortKey));
                if (sortTerm is not null)
                {
                    keyTerms.Add(sortTerm);
                    terms.Remove(sortTerm);
                }
            }
        }

        var translator = new ExpressionTranslator(metadata);
        string? keyExpression = null;
        string? filterExpression = null;

        if (keyTerms.Count > 0)
        {
            keyExpression = translator.Translate(CombineAnd(keyTerms)!).Expression;
        }

        if (terms.Count > 0)
        {
            filterExpression = translator.Translate(CombineAnd(terms)!).Expression;
        }

        var translated = translator.Snapshot();
        return new QueryPlan(
            isQuery,
            keyExpression,
            filterExpression,
            translated.Names,
            translated.Values);
    }

    private static IEnumerable<Expression> FlattenAnd(Expression expression)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } binary)
        {
            return FlattenAnd(binary.Left).Concat(FlattenAnd(binary.Right));
        }

        return [expression];
    }

    private static Expression? CombineAnd(IReadOnlyList<Expression> terms)
    {
        if (terms.Count == 0)
        {
            return null;
        }

        return terms.Skip(1).Aggregate(terms[0], Expression.AndAlso);
    }

    private static bool IsSortCondition(Expression expression, PropertyMetadata property)
    {
        if (expression is MethodCallExpression call &&
            call.Method.Name == nameof(string.StartsWith) &&
            call.Object is MemberExpression member)
        {
            return member.Member == property.Property;
        }

        return IsComparisonOn(
            expression,
            property,
            ExpressionType.Equal,
            ExpressionType.LessThan,
            ExpressionType.LessThanOrEqual,
            ExpressionType.GreaterThan,
            ExpressionType.GreaterThanOrEqual);
    }

    private static bool IsComparisonOn(
        Expression expression,
        PropertyMetadata property,
        params ExpressionType[] allowedTypes)
    {
        if (expression is not BinaryExpression binary || !allowedTypes.Contains(binary.NodeType))
        {
            return false;
        }

        return IsProperty(binary.Left, property) || IsProperty(binary.Right, property);
    }

    private static bool IsProperty(Expression expression, PropertyMetadata property)
    {
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
            } unary)
        {
            expression = unary.Operand;
        }

        return expression is MemberExpression member && member.Member == property.Property;
    }
}