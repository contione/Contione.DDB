using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository;

internal sealed record QueryPlan(
    bool IsQuery,
    string? KeyExpression,
    string? FilterExpression,
    Dictionary<string, string> Names,
    Dictionary<string, AttributeValue> Values);

internal static class QueryPlanner
{
    public static QueryPlan Create<TEntity>(
        IReadOnlyList<Expression<Func<TEntity, bool>>> predicates,
        string? indexName,
        IDynamoDBContext? context = null) where TEntity : class
    {
        var metadata = EntityMetadata.For<TEntity>();
        var schema = metadata.GetKeySchema(indexName);
        var translator = new ExpressionTranslator(metadata, context);
        var terms = predicates
            .SelectMany(predicate => FlattenAnd(predicate.Body)
                .Select(expression => new QueryTerm(predicate.Parameters.Single(), expression)))
            .ToList();

        var partitionConditions = terms
            .Select(term => TryGetComparison(term, schema.PartitionKey, out var condition)
                ? condition
                : null)
            .Where(static condition => condition is { Operator: ExpressionType.Equal })
            .Cast<KeyCondition>()
            .ToArray();

        if (partitionConditions.Length == 0)
        {
            var filters = terms
                .Select(term => translator.Translate(term.Expression).Expression)
                .ToArray();
            var translatedScan = translator.Snapshot();
            return new QueryPlan(
                false,
                null,
                filters.Length == 0 ? null : string.Join(" AND ", filters),
                translatedScan.Names,
                translatedScan.Values);
        }

        var partitionTerms = partitionConditions
            .Select(static condition => condition.Term)
            .ToHashSet();

        if (partitionConditions.Length > 0)
        {
            var partitionValue = ToKeyValue(
                partitionConditions[0],
                schema.PartitionKey,
                translator,
                validateKey: true);
            foreach (var condition in partitionConditions.Skip(1))
            {
                var value = ToKeyValue(condition, schema.PartitionKey, translator, validateKey: true);
                if (!AreEquivalent(partitionValue, value))
                {
                    throw new NotSupportedException(
                        "A DynamoDB query cannot use conflicting partition key conditions.");
                }
            }
        }

        var remainingTerms = terms.Except(partitionTerms).ToList();
        if (remainingTerms.Any(term => HasEntityProperty(
            term.Expression,
            term.EntityParameter,
            schema.PartitionKey.Property)))
        {
            throw new NotSupportedException(
                "A DynamoDB query requires one equality condition for the partition key; " +
                "other partition key conditions cannot be used in a filter or scan.");
        }

        KeyCondition? sortCondition = null;
        KeyCondition? lowerBound = null;
        KeyCondition? upperBound = null;
        var sortTerms = new List<QueryTerm>();
        if (schema.SortKey is not null)
        {
            var sortKey = schema.SortKey;
            sortTerms = remainingTerms
                .Where(term => HasEntityProperty(term.Expression, term.EntityParameter, sortKey.Property))
                .ToList();

            var sortConditions = new List<KeyCondition>(sortTerms.Count);
            foreach (var term in sortTerms)
            {
                if (!TryGetSortCondition(term, sortKey, out var condition))
                {
                    throw new NotSupportedException(
                        "The sort key condition cannot be represented by a DynamoDB query.");
                }

                sortConditions.Add(condition);
            }

            if (sortConditions.Count == 1)
            {
                sortCondition = sortConditions[0];
                if (sortCondition.ValueExpression is not null)
                {
                    _ = ToKeyValue(
                        sortCondition,
                        sortKey,
                        translator,
                        validateKey: sortCondition.Operator is not null);
                }
            }
            else if (sortConditions.Count == 2 &&
                sortConditions.All(static condition => condition.Operator is not null) &&
                sortConditions.Count(static condition =>
                    condition.Operator == ExpressionType.GreaterThanOrEqual) == 1 &&
                sortConditions.Count(static condition =>
                    condition.Operator == ExpressionType.LessThanOrEqual) == 1)
            {
                lowerBound = sortConditions.Single(static condition =>
                    condition.Operator == ExpressionType.GreaterThanOrEqual);
                upperBound = sortConditions.Single(static condition =>
                    condition.Operator == ExpressionType.LessThanOrEqual);

                var lowerValue = ToKeyValue(lowerBound, sortKey, translator, validateKey: true);
                var upperValue = ToKeyValue(upperBound, sortKey, translator, validateKey: true);
                var comparison = CompareKeyValues(lowerValue, upperValue);
                if (comparison > 0)
                {
                    throw new NotSupportedException(
                        "The sort key lower bound cannot be greater than its upper bound.");
                }
            }
            else if (sortConditions.Count > 1)
            {
                throw new NotSupportedException(
                    "DynamoDB supports one sort key condition, or one inclusive lower and upper bound.");
            }

            remainingTerms = remainingTerms.Except(sortTerms).ToList();
        }

        if (remainingTerms.Any(term => HasEntityProperty(
            term.Expression,
            term.EntityParameter,
            schema.PartitionKey.Property) ||
            schema.SortKey is not null && HasEntityProperty(
                term.Expression,
                term.EntityParameter,
                schema.SortKey.Property)))
        {
            throw new NotSupportedException(
                "DynamoDB key attributes cannot appear in a filter expression.");
        }

        var keyExpressions = new List<string>(2);
        if (partitionConditions.Length > 0)
        {
            keyExpressions.Add(translator.Translate(partitionConditions[0].Term.Expression).Expression);
        }

        if (lowerBound is not null && upperBound is not null)
        {
            keyExpressions.Add(translator.TranslateBetween(
                lowerBound.KeyExpression,
                lowerBound.ValueExpression!,
                upperBound.KeyExpression,
                upperBound.ValueExpression!).Expression);
        }
        else if (sortCondition is not null)
        {
            keyExpressions.Add(translator.Translate(sortCondition.Term.Expression).Expression);
        }

        var filterExpressions = remainingTerms
            .Select(term => translator.Translate(term.Expression).Expression)
            .ToArray();
        var translated = translator.Snapshot();

        return new QueryPlan(
            partitionConditions.Length > 0,
            keyExpressions.Count == 0 ? null : string.Join(" AND ", keyExpressions),
            filterExpressions.Length == 0 ? null : string.Join(" AND ", filterExpressions),
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

    private static bool TryGetSortCondition(
        QueryTerm term,
        PropertyMetadata property,
        out KeyCondition condition)
    {
        if (term.Expression is MethodCallExpression
            {
                Method.DeclaringType: not null,
                Method.Name: nameof(string.StartsWith),
                Arguments.Count: 1,
                Object: not null,
            } call &&
            call.Method.DeclaringType == typeof(string) &&
            ExpressionTranslator.IsEntityPropertyAccess(
                call.Object,
                term.EntityParameter,
                property))
        {
            condition = new KeyCondition(term, call.Object, call.Arguments[0], null);
            return true;
        }

        return TryGetComparison(term, property, out condition) &&
            condition.Operator is ExpressionType.Equal or
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
    }

    private static bool TryGetComparison(
        QueryTerm term,
        PropertyMetadata property,
        out KeyCondition condition)
    {
        if (term.Expression is BinaryExpression binary && IsComparison(binary.NodeType))
        {
            if (ExpressionTranslator.IsEntityPropertyAccess(
                binary.Left,
                term.EntityParameter,
                property))
            {
                condition = new KeyCondition(
                    term,
                    binary.Left,
                    binary.Right,
                    binary.NodeType);
                return true;
            }

            if (ExpressionTranslator.IsEntityPropertyAccess(
                binary.Right,
                term.EntityParameter,
                property))
            {
                condition = new KeyCondition(
                    term,
                    binary.Right,
                    binary.Left,
                    Reverse(binary.NodeType));
                return true;
            }
        }

        condition = null!;
        return false;
    }

    private static bool HasEntityProperty(
        Expression expression,
        ParameterExpression entityParameter,
        PropertyInfo property)
    {
        var visitor = new EntityPropertyVisitor(entityParameter, property);
        visitor.Visit(expression);
        return visitor.Found;
    }

    private static AttributeValue ToKeyValue(
        KeyCondition condition,
        PropertyMetadata property,
        ExpressionTranslator translator,
        bool validateKey)
    {
        var value = translator.TranslateValue(
            condition.ValueExpression!,
            property,
            condition.KeyExpression,
            condition.Operator ?? ExpressionType.Equal);
        if (validateKey)
        {
            AttributeValueConverter.ValidateKey(value, property);
            return value;
        }

        if (value.S is null && value.N is null && value.B is null)
        {
            throw new NotSupportedException(
                "DynamoDB key conditions require a string, number, or binary value.");
        }

        return value;
    }

    private static bool AreEquivalent(AttributeValue first, AttributeValue second)
    {
        if (first.S is not null || second.S is not null)
        {
            return first.S == second.S;
        }

        if (first.N is not null || second.N is not null)
        {
            if (decimal.TryParse(first.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) &&
                decimal.TryParse(second.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
            {
                return left == right;
            }

            return first.N == second.N;
        }

        if (first.B is not null || second.B is not null)
        {
            return first.B is not null && second.B is not null &&
                first.B.ToArray().AsSpan().SequenceEqual(second.B.ToArray());
        }

        return false;
    }

    private static int CompareKeyValues(AttributeValue lower, AttributeValue upper)
    {
        if (lower.N is not null && upper.N is not null)
        {
            if (!decimal.TryParse(lower.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var lowerNumber) ||
                !decimal.TryParse(upper.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var upperNumber))
            {
                return 0;
            }

            return lowerNumber.CompareTo(upperNumber);
        }

        if (lower.S is not null && upper.S is not null)
        {
            return Encoding.UTF8.GetBytes(lower.S).AsSpan()
                .SequenceCompareTo(Encoding.UTF8.GetBytes(upper.S));
        }

        if (lower.B is not null && upper.B is not null)
        {
            return lower.B.ToArray().AsSpan().SequenceCompareTo(upper.B.ToArray());
        }

        throw new NotSupportedException(
            "Sort key range bounds must use the same DynamoDB scalar type.");
    }

    private static bool IsComparison(ExpressionType type) => type is
        ExpressionType.Equal or ExpressionType.NotEqual or
        ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    private static ExpressionType Reverse(ExpressionType type) => type switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => type,
    };

    private sealed record QueryTerm(ParameterExpression EntityParameter, Expression Expression);

    private sealed record KeyCondition(
        QueryTerm Term,
        Expression KeyExpression,
        Expression? ValueExpression,
        ExpressionType? Operator);

    private sealed class EntityPropertyVisitor(
        ParameterExpression entityParameter,
        PropertyInfo property) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == entityParameter && node.Member == property)
            {
                Found = true;
            }

            return Found ? node : base.VisitMember(node);
        }
    }
}
