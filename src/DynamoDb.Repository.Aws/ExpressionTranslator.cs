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
            IsSafeConversion(unary.Operand.Type, unary.Type)
                ? Visit(unary.Operand)
                : throw Unsupported(unary),
        MethodCallExpression call => VisitMethodCall(call),
        MemberExpression member when TryGetMappedProperty(member, out var property) =>
            $"{AddName(property.AttributeName)} = {AddValue(true)}",
        ConstantExpression { Type: var type, Value: bool value } when type == typeof(bool) =>
            value ? AddAlwaysTrue() : AddAlwaysFalse(),
        _ => throw Unsupported(expression),
    };

    private string VisitComparison(BinaryExpression binary)
    {
        if (TryGetMappedProperty(binary.Left, out var leftProperty))
        {
            return $"{AddName(leftProperty.AttributeName)} {ComparisonOperator(binary.NodeType)} " +
                AddValue(EvaluateComparisonValue(binary.Left, binary.Right, leftProperty, binary.NodeType));
        }

        if (TryGetMappedProperty(binary.Right, out var rightProperty))
        {
            return $"{AddName(rightProperty.AttributeName)} {ComparisonOperator(Reverse(binary.NodeType))} " +
                AddValue(EvaluateComparisonValue(binary.Right, binary.Left, rightProperty, binary.NodeType));
        }

        throw Unsupported(binary);
    }

    private string VisitMethodCall(MethodCallExpression call)
    {
        if (call.Method.DeclaringType == typeof(string) &&
            call.Object is not null &&
            TryGetMappedProperty(call.Object, out var stringProperty) &&
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

            if (TryGetMappedProperty(item, out var itemProperty))
            {
                var values = EvaluateCollection(collection) as IEnumerable
                    ?? throw new NotSupportedException("Contains source must be an enumerable constant.");
                var items = new List<object?>(100);
                foreach (var value in values)
                {
                    if (items.Count == 100)
                    {
                        throw new NotSupportedException(
                            "DynamoDB IN expressions support at most 100 values.");
                    }

                    items.Add(value);
                }

                if (items.Count == 0)
                {
                    return AddAlwaysFalse();
                }

                var tokens = items.Select(AddValue).ToArray();
                return $"{AddName(itemProperty.AttributeName)} IN ({string.Join(", ", tokens)})";
            }

            if (TryGetMappedProperty(collection, out var collectionProperty))
            {
                return $"contains({AddName(collectionProperty.AttributeName)}, {AddValue(Evaluate(item))})";
            }
        }

        throw Unsupported(call);
    }

    private bool TryGetMappedProperty(Expression expression, out PropertyMetadata property)
    {
        var conversions = new List<(Type Source, Type Destination)>();
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
            } unary)
        {
            conversions.Add((unary.Operand.Type, unary.Type));
            expression = unary.Operand;
        }

        if (expression is MemberExpression { Expression: ParameterExpression } member)
        {
            property = metadata.GetProperty(member.Member);
            var propertyType = property.Property.PropertyType;
            if (conversions.All(conversion => IsSafePropertyConversion(
                conversion.Source,
                conversion.Destination,
                propertyType)))
            {
                return true;
            }
        }

        property = null!;
        return false;
    }

    internal TranslatedExpression TranslateBetween(
        Expression propertyExpression,
        Expression lowerBound,
        Expression upperBound)
    {
        if (!TryGetMappedProperty(propertyExpression, out var property))
        {
            throw Unsupported(propertyExpression);
        }

        var lowerValue = EvaluateComparisonValue(
            propertyExpression,
            lowerBound,
            property,
            ExpressionType.GreaterThanOrEqual);
        var upperValue = EvaluateComparisonValue(
            propertyExpression,
            upperBound,
            property,
            ExpressionType.LessThanOrEqual);
        var expression =
            $"{AddName(property.AttributeName)} BETWEEN {AddValue(lowerValue)} AND {AddValue(upperValue)}";
        return new TranslatedExpression(expression, _names, _values);
    }

    internal static AttributeValue ToAttributeValue(Expression expression) =>
        AttributeValueConverter.FromObject(Evaluate(expression));

    internal static bool IsEntityPropertyAccess(
        Expression expression,
        ParameterExpression entityParameter,
        PropertyMetadata property)
    {
        var conversions = new List<(Type Source, Type Destination)>();
        while (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
            } unary)
        {
            conversions.Add((unary.Operand.Type, unary.Type));
            expression = unary.Operand;
        }

        return expression is MemberExpression
        {
            Expression: ParameterExpression parameter,
        } member && parameter == entityParameter && member.Member == property.Property &&
            conversions.All(conversion => IsSafePropertyConversion(
                conversion.Source,
                conversion.Destination,
                property.Property.PropertyType));
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

    private static object? EvaluateComparisonValue(
        Expression propertyExpression,
        Expression valueExpression,
        PropertyMetadata property,
        ExpressionType comparison)
    {
        var value = Evaluate(valueExpression);
        var propertyType = Nullable.GetUnderlyingType(property.Property.PropertyType)
            ?? property.Property.PropertyType;
        var comparisonType = Nullable.GetUnderlyingType(propertyExpression.Type)
            ?? propertyExpression.Type;

        if (propertyType.IsEnum && comparisonType != propertyType)
        {
            if (comparison is not (ExpressionType.Equal or ExpressionType.NotEqual))
            {
                throw new NotSupportedException(
                    "Ordering an enum after converting it to its numeric value cannot be represented by its string storage.");
            }

            if (value is null)
            {
                return null;
            }

            var underlyingType = Enum.GetUnderlyingType(propertyType);
            return Enum.ToObject(
                propertyType,
                Convert.ChangeType(value, underlyingType, System.Globalization.CultureInfo.InvariantCulture)!);
        }

        if (propertyType == typeof(char) && comparisonType != propertyType && value is not null)
        {
            return Convert.ToChar(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return value;
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

    private static bool IsSafeConversion(Type sourceType, Type destinationType)
    {
        var sourceNullableType = Nullable.GetUnderlyingType(sourceType);
        var destinationNullableType = Nullable.GetUnderlyingType(destinationType);
        if (sourceNullableType is not null && destinationNullableType is null)
        {
            return false;
        }

        var source = sourceNullableType ?? sourceType;
        var destination = destinationNullableType ?? destinationType;
        if (source == destination)
        {
            return true;
        }

        return IsExactNumericWidening(source, destination);
    }

    private static bool IsSafePropertyConversion(
        Type sourceType,
        Type destinationType,
        Type propertyType)
    {
        var sourceNullableType = Nullable.GetUnderlyingType(sourceType);
        var destinationNullableType = Nullable.GetUnderlyingType(destinationType);
        if (sourceNullableType is not null && destinationNullableType is null)
        {
            return false;
        }

        var source = sourceNullableType ?? sourceType;
        var destination = destinationNullableType ?? destinationType;
        var property = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (source == destination)
        {
            return true;
        }

        if (source == property && property.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(property);
            return destination == underlying || IsExactNumericWidening(underlying, destination);
        }

        if (source == property && property == typeof(char))
        {
            return destination == typeof(ushort) ||
                IsExactNumericWidening(typeof(ushort), destination);
        }

        return IsExactNumericWidening(source, destination);
    }

    private static bool IsExactNumericWidening(Type source, Type destination) =>
        Type.GetTypeCode(source) switch
        {
            TypeCode.SByte => destination == typeof(short) || destination == typeof(int) ||
                destination == typeof(long) || destination == typeof(decimal),
            TypeCode.Byte => destination == typeof(short) || destination == typeof(ushort) ||
                destination == typeof(int) || destination == typeof(uint) ||
                destination == typeof(long) || destination == typeof(ulong) ||
                destination == typeof(decimal),
            TypeCode.Int16 => destination == typeof(int) || destination == typeof(long) ||
                destination == typeof(decimal),
            TypeCode.UInt16 => destination == typeof(int) || destination == typeof(uint) ||
                destination == typeof(long) || destination == typeof(ulong) ||
                destination == typeof(decimal),
            TypeCode.Int32 => destination == typeof(long) || destination == typeof(decimal),
            TypeCode.UInt32 => destination == typeof(long) || destination == typeof(ulong) ||
                destination == typeof(decimal),
            TypeCode.Int64 or TypeCode.UInt64 => destination == typeof(decimal),
            _ => false,
        };

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
