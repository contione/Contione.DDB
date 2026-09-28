using System.Globalization;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;

namespace Accounts.Domain;

/// <summary>Preserves the existing UTC ISO 8601 representation used by the accounts index.</summary>
public sealed class UtcDateTimeOffsetConverter : IPropertyConverter
{
    public DynamoDBEntry ToEntry(object value) => ((DateTimeOffset)value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public object FromEntry(DynamoDBEntry entry) => DateTimeOffset.Parse(entry.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
