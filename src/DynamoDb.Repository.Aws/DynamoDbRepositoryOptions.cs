namespace DynamoDb.Repository.Aws;

public sealed class DynamoDbRepositoryOptions
{
    public const string SectionName = "DynamoDbRepository";

    public string TableNamePrefix { get; set; } = string.Empty;

    public int DefaultFetchSize { get; set; } = 100;

    public int MaxPageSize { get; set; } = 1_000;
}