namespace DynamoDb.Repository.Aws;

public sealed class DynamoDbRepositoryOptions
{
    public const string SectionName = "DynamoDbRepository";

    public string TableNamePrefix { get; set; } = string.Empty;

    public int DefaultFetchSize { get; set; } = 100;

    public int MaxPageSize { get; set; } = 1_000;

    /// <summary>Allows queries without a partition-key equality to scan a table or index.</summary>
    public bool AllowScan { get; set; }

    /// <summary>Maximum SDK calls made by one terminal query operation, excluding SDK retries.</summary>
    public int MaxRequestsPerOperation { get; set; } = 100;

    /// <summary>Maximum items evaluated before filtering by one terminal query operation.</summary>
    public int MaxEvaluatedItems { get; set; } = 10_000;
}
