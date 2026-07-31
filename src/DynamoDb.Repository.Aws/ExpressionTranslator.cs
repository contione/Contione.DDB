using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository.Aws;

internal sealed record TranslatedExpression(
    string Expression,
    Dictionary<string, string> Names,
    Dictionary<string, AttributeValue> Values);

internal sealed class ExpressionTranslator(EntityMetadata metadata)
{
    private readonly Dictionary<string, string> _nameTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttributeValue> _values = new(StringComparer.Ordinal);
    private int _nameIndex;
    private int _valueIndex;

    public TranslatedExpression Translate(Expression expression) =>
        new(Visit(expression), _names, _values);

    public TranslatedExpression Snapshot() => new(string.Empty, _names, _values);

    private string Visit(Expression expression) => expression switch
    {
        BinaryExpression binary when IsLogical(binary.NodeType) =>
            $"({Visit(binary.Left)} {LogicalOperator(binary.NodeType)} {Visit(binary.Right)})",
        BinaryExpression binary when IsComparison(binary.NodeType) => VisitComparison(binary),
        UnaryExpression { NodeType: ExpressionType.Not } unary => $"(NOT {Visit(unary.Operand)})",
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary =>
            Visit(unary.Operand),
        MethodCallExpression call => VisitMethodCall(call),
        MemberExpression member when TryGetMappedProperty(member, out var property) =>
            $"{AddName(property.AttributeName)} = {AddValue(true)}",
        ConstantExpression { Type: var type, Value: bool value } when type == typeof(bool) =>
            value ? AddAlwaysTrue() : AddAlwaysFalse(),
        _ => throw Unsupported(expression),
    };

    private string VisitComparison(BinaryExpression binary)
    {
        if (TryGetMappedProperty(Unwrap(binary.Left), out var leftProperty))
        {
            return $"{AddName(leftProperty.AttributeName)} {ComparisonOperator(binary.NodeType)} " +
                AddValue(Evaluate(binary.Right));
        }

        if (TryGetMappedProperty(Unwrap(binary.Right), out var rightProperty))
        {
            return $"{AddName(rightProperty.AttributeName)} {ComparisonOperator(Reverse(binary.NodeType))} " +
                AddValue(Evaluate(binary.Left));
        }

        throw Unsupported(binary);
    }

    private string VisitMethodCall(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == typeof(string) &&
            call.Object is not null &&
            TryGetMappedProperty(Unwrap(call.Object), out var stringProperty) &&
            call.Arguments.Count == 1)
        {
            var function = call.Method.Name switch
            {
                nameof(string.StartsWith) => "begins_with",
                nameof(string.Contains) => "contains",
                _ => throw Unsupported(call),
            };

            return $"{function}({AddName(stringProperty.AttributeName)}, {AddValue(Evaluate(call.Arguments[0]))})";
        }

        if (call.Method.Name == nameof(Enumerable.Contains))
        {
            Expression collection;
            Expression item;
            if (call.Object is not null)
            {
                collection = call.Object;
                item = call.Arguments[0];
            }
            else if (call.Arguments.Count == 2)
            {
                collection = call.Arguments[0];
                item = call.Arguments[1];
            }
            else
            {
                throw Unsupported(call);
            }

            if (TryGetMappedProperty(Unwrap(item), out var itemProperty))
            {
                var values = EvaluateCollection(collection) as IEnumerable
                    ?? throw new NotSupportedException("Contains source must be an enumerable constant.");
                var tokens = values.Cast<object?>().Select(AddValue).ToArray();
                if (tokens.Length == 0)
                {
                    return AddAlwaysFalse();
                }

                return $"{AddName(itemProperty.AttributeName)} IN ({string.Join(", ", tokens)})";
            }

            if (TryGetMappedProperty(Unwrap(collection), out var collectionProperty))
            {
                return $"contains({AddName(collectionProperty.AttributeName)}, {AddValue(Evaluate(item))})";
            }
        }

        throw Unsupported(call);
    }

    private bool TryGetMappedProperty(Expression expression, out PropertyMetadata property)
    {
        if (expression is MemberExpression { Expression: ParameterExpression } member)
        {
            property = metadata.GetProperty(member.Member);
            return true;
        }

        property = null!;
        return false;
    }

    private string AddName(string attributeName)
    {
        if (_nameTokens.TryGetValue(attributeName, out var token))
        {
            return token;
        }

        token = $"#n{_nameIndex++}";
        _nameTokens[attributeName] = token;
        _names[token] = attributeName;
        return token;
    }

    private string AddValue(object? value)
    {
        var token = $":v{_valueIndex++}";
        _values[token] = AttributeValueConverter.FromObject(value);
        return token;
    }

    private string AddAlwaysTrue()
    {
        var token = AddValue(1);
        return $"{token} = {token}";
    }

    private string AddAlwaysFalse()
    {
        var left = AddValue(1);
        var right = AddValue(0);
        return $"{left} = {right}";
    }

    private static object? Evaluate(Expression expression)
    {
        if (ContainsParameter(expression))
        {
            throw Unsupported(expression);
        }

        return expression switch
        {
            ConstantExpression constant => constant.Value,
            MemberExpression member => EvaluateMember(member),
            UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
            } unary => ConvertValue(Evaluate(unary.Operand), unary.Type),
            NewArrayExpression array => EvaluateArray(array),
            _ => throw new NotSupportedException(
                $"Value expression '{expression}' must be a constant, captured member, or array."),
        };
    }

    private static object? EvaluateCollection(Expression expression) =>
        expression is MethodCallExpression
        {
            Method.Name: "op_Implicit",
            Arguments.Count: 1,
        } conversion
            ? Evaluate(conversion.Arguments[0])
            : Evaluate(expression);

    private static object? EvaluateMember(MemberExpression member)
    {
        var target = member.Expression is null ? null : Evaluate(member.Expression);
        if (member.Member.DeclaringType is { } declaringType &&
            Nullable.GetUnderlyingType(declaringType) is not null)
        {
            return member.Member.Name switch
            {
                nameof(Nullable<int>.Value) => target,
                nameof(Nullable<int>.HasValue) => target is not null,
                _ => throw Unsupported(member),
            };
        }

        return member.Member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property => property.GetValue(target),
            _ => throw Unsupported(member),
        };
    }

    private static Array EvaluateArray(NewArrayExpression expression)
    {
        var elementType = expression.Type.GetElementType()
            ?? throw Unsupported(expression);
        var result = Array.CreateInstance(elementType, expression.Expressions.Count);
        for (var index = 0; index < expression.Expressions.Count; index++)
        {
            result.SetValue(Evaluate(expression.Expressions[index]), index);
        }

        return result;
    }

    private static object? ConvertValue(object? value, Type destinationType)
    {
        if (value is null || destinationType.IsInstanceOfType(value))
        {
            return value;
        }

        var targetType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
        return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ContainsParameter(Expression expression)
    {
        var visitor = new ParameterFindingVisitor();
        visitor.Visit(expression);
        return visitor.Found;
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
            } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static bool IsLogical(ExpressionType type) =>
        type is ExpressionType.AndAlso or ExpressionType.OrElse;

    private static bool IsComparison(ExpressionType type) => type is
        ExpressionType.Equal or ExpressionType.NotEqual or
        ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
        ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;

    private static string LogicalOperator(ExpressionType type) =>
        type == ExpressionType.AndAlso ? "AND" : "OR";

    private static string ComparisonOperator(ExpressionType type) => type switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "<>",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static ExpressionType Reverse(ExpressionType type) => type switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => type,
    };

    private static NotSupportedException Unsupported(Expression expression) =>
        new($"Expression '{expression}' is not supported by DynamoDB.");

    private sealed class ParameterFindingVisitor : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }
}