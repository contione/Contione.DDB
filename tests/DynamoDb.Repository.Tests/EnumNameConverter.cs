using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;

namespace DynamoDb.Repository.Tests;

// Explicit opt-in keeps pre-SDK string enum data compatible.
public sealed class EnumNameConverter<TEnum> : IPropertyConverter where TEnum : struct, Enum
{
    public DynamoDBEntry ToEntry(object value) => ((TEnum)value).ToString();

    public object FromEntry(DynamoDBEntry entry) => Enum.Parse<TEnum>(entry.AsString());
}
