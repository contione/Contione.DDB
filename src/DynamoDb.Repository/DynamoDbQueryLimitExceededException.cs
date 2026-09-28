namespace DynamoDb.Repository;

/// <summary>A query exhausted its configured request or evaluated-item budget.</summary>
public sealed class DynamoDbQueryLimitExceededException(int requests, int evaluatedItems)
    : InvalidOperationException(
        $"DynamoDB query exceeded its execution budget after {requests} requests and {evaluatedItems} evaluated items. Narrow the query or explicitly increase the configured limits.")
{
    public int Requests { get; } = requests;

    public int EvaluatedItems { get; } = evaluatedItems;
}
