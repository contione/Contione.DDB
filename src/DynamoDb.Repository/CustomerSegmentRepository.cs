using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace DynamoDb.Repository;

/// <summary>Reads customer segment identifiers with a paged DynamoDB Query.</summary>
/// <remarks>
/// The table must use the customer identifier as its partition key and the segment
/// identifier as its sort key. Attribute names are configurable so applications can
/// keep their existing naming conventions.
/// </remarks>
public sealed class CustomerSegmentRepository
{
    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly string _customerIdAttributeName;
    private readonly string _segmentIdAttributeName;

    public CustomerSegmentRepository(
        IAmazonDynamoDB client,
        string tableName,
        string customerIdAttributeName,
        string segmentIdAttributeName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerIdAttributeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentIdAttributeName);

        _client = client;
        _tableName = tableName;
        _customerIdAttributeName = customerIdAttributeName;
        _segmentIdAttributeName = segmentIdAttributeName;
    }

    /// <summary>Returns all segment identifiers for one customer.</summary>
    /// <remarks>Query pagination is handled internally. Reads use eventual consistency by default.</remarks>
    public async Task<IReadOnlyList<string>> GetSegmentIdsAsync(
        string customerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var segmentIds = new List<string>();
        Dictionary<string, AttributeValue>? lastEvaluatedKey = null;
        do
        {
            var response = await _client.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "#customerId = :customerId",
                ProjectionExpression = "#segmentId",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#customerId"] = _customerIdAttributeName,
                    ["#segmentId"] = _segmentIdAttributeName,
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":customerId"] = new() { S = customerId },
                },
                ExclusiveStartKey = lastEvaluatedKey,
                ConsistentRead = false,
            }, cancellationToken).ConfigureAwait(false);

            if (response.Items is not null)
            {
                foreach (var item in response.Items)
                {
                    if (item.TryGetValue(_segmentIdAttributeName, out var value) && !string.IsNullOrEmpty(value.S))
                    {
                        segmentIds.Add(value.S);
                    }
                }
            }

            lastEvaluatedKey = response.LastEvaluatedKey;
        }
        while (lastEvaluatedKey is { Count: > 0 });

        return segmentIds.AsReadOnly();
    }
}
