using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace DynamoDb.Repository.Aws;

public sealed class AwsDynamoDbRepository<TEntity> : IDynamoDbRepository<TEntity>
    where TEntity : class
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbRepositoryOptions _options;
    private readonly EntityMetadata _metadata = EntityMetadata.For<TEntity>();
    private readonly EntityMapper<TEntity> _mapper = new();
    private readonly string _tableName;

    public AwsDynamoDbRepository(
        IAmazonDynamoDB client,
        IOptions<DynamoDbRepositoryOptions> options)
    {
        _client = client;
        _options = options.Value;
        ValidateOptions(_options);
        _tableName = string.Concat(_options.TableNamePrefix, _metadata.TableName);
    }

    public IDynamoDbQuery<TEntity> Query => new DynamoDbQuery<TEntity>(this);

    internal int FetchSize => _options.DefaultFetchSize;

    public async Task<TEntity?> GetAsync(
        object partitionKey,
        object? sortKey = null,
        bool consistentRead = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = CreateKey(partitionKey, sortKey),
            ConsistentRead = consistentRead,
        }, cancellationToken).ConfigureAwait(false);

        return response.Item is { Count: > 0 } ? _mapper.FromMap(response.Item) : null;
    }

    public async Task PutAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _client.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = _mapper.ToMap(entity),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        object partitionKey,
        object? sortKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        await _client.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = CreateKey(partitionKey, sortKey),
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PageResult<TEntity>> ExecutePageAsync(
        QueryState<TEntity> state,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ValidatePageSize(pageSize);
        ValidateState(state);

        var requestedCount = state.Take is int take ? Math.Min(take, pageSize) : pageSize;
        var plan = QueryPlanner.Create(state.Predicates, state.IndexName);
        ValidatePlan(state, plan);

        var items = new List<TEntity>(requestedCount);
        var scannedCount = 0;
        var lastKey = ContinuationTokenCodec.Decode(continuationToken);

        do
        {
            var remaining = requestedCount - items.Count;
            if (plan.IsQuery)
            {
                var response = await _client.QueryAsync(CreateQueryRequest(
                    state,
                    plan,
                    remaining,
                    lastKey), cancellationToken).ConfigureAwait(false);
                scannedCount += response.ScannedCount ?? 0;
                AddItems(response.Items, items);
                lastKey = response.LastEvaluatedKey;
            }
            else
            {
                var response = await _client.ScanAsync(CreateScanRequest(
                    state,
                    plan,
                    remaining,
                    lastKey), cancellationToken).ConfigureAwait(false);
                scannedCount += response.ScannedCount ?? 0;
                AddItems(response.Items, items);
                lastKey = response.LastEvaluatedKey;
            }
        }
        while (items.Count < requestedCount && lastKey is { Count: > 0 });

        return new PageResult<TEntity>(items, ContinuationTokenCodec.Encode(lastKey), scannedCount);
    }

    internal async Task<int> ExecuteCountAsync(
        QueryState<TEntity> state,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        var plan = QueryPlanner.Create(state.Predicates, state.IndexName);
        ValidatePlan(state, plan);
        Dictionary<string, AttributeValue>? lastKey = null;
        var total = 0;

        do
        {
            var remaining = state.Take is int take
                ? Math.Min(take - total, _options.DefaultFetchSize)
                : _options.DefaultFetchSize;
            if (remaining <= 0)
            {
                break;
            }

            if (plan.IsQuery)
            {
                var request = CreateQueryRequest(state, plan, remaining, lastKey);
                request.Select = Select.COUNT;
                var response = await _client.QueryAsync(request, cancellationToken).ConfigureAwait(false);
                total += response.Count ?? 0;
                lastKey = response.LastEvaluatedKey;
            }
            else
            {
                var request = CreateScanRequest(state, plan, remaining, lastKey);
                request.Select = Select.COUNT;
                var response = await _client.ScanAsync(request, cancellationToken).ConfigureAwait(false);
                total += response.Count ?? 0;
                lastKey = response.LastEvaluatedKey;
            }
        }
        while (lastKey is { Count: > 0 });

        return total;
    }

    internal PropertyMetadata GetOrderProperty(System.Linq.Expressions.LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is System.Linq.Expressions.UnaryExpression
            {
                NodeType: System.Linq.Expressions.ExpressionType.Convert or
                       System.Linq.Expressions.ExpressionType.ConvertChecked,
            } unary)
        {
            body = unary.Operand;
        }

        return body is System.Linq.Expressions.MemberExpression member &&
               member.Expression is System.Linq.Expressions.ParameterExpression
            ? _metadata.GetProperty(member.Member)
            : throw new ArgumentException("Order selector must be a mapped property.", nameof(selector));
    }

    private QueryRequest CreateQueryRequest(
        QueryState<TEntity> state,
        QueryPlan plan,
        int limit,
        Dictionary<string, AttributeValue>? lastKey) => new()
        {
            TableName = _tableName,
            IndexName = state.IndexName,
            KeyConditionExpression = plan.KeyExpression,
            FilterExpression = plan.FilterExpression,
            ExpressionAttributeNames = plan.Names.Count == 0 ? null : plan.Names,
            ExpressionAttributeValues = plan.Values.Count == 0 ? null : plan.Values,
            ExclusiveStartKey = lastKey,
            Limit = limit,
            ConsistentRead = state.ConsistentRead,
            ScanIndexForward = !state.Descending,
        };

    private ScanRequest CreateScanRequest(
        QueryState<TEntity> state,
        QueryPlan plan,
        int limit,
        Dictionary<string, AttributeValue>? lastKey) => new()
        {
            TableName = _tableName,
            IndexName = state.IndexName,
            FilterExpression = plan.FilterExpression,
            ExpressionAttributeNames = plan.Names.Count == 0 ? null : plan.Names,
            ExpressionAttributeValues = plan.Values.Count == 0 ? null : plan.Values,
            ExclusiveStartKey = lastKey,
            Limit = limit,
            ConsistentRead = state.ConsistentRead,
        };

    private Dictionary<string, AttributeValue> CreateKey(object partitionKey, object? sortKey)
    {
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [_metadata.PrimaryKey.PartitionKey.AttributeName] =
                AttributeValueConverter.FromObject(partitionKey),
        };

        if (_metadata.PrimaryKey.SortKey is { } sortProperty)
        {
            if (sortKey is null)
            {
                throw new ArgumentNullException(nameof(sortKey), "This entity requires a sort key.");
            }

            key[sortProperty.AttributeName] = AttributeValueConverter.FromObject(sortKey);
        }
        else if (sortKey is not null)
        {
            throw new ArgumentException("This entity does not define a sort key.", nameof(sortKey));
        }

        return key;
    }

    private void AddItems(
        List<Dictionary<string, AttributeValue>>? source,
        ICollection<TEntity> destination)
    {
        if (source is null)
        {
            return;
        }

        foreach (var item in source)
        {
            destination.Add(_mapper.FromMap(item));
        }
    }

    private void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 || pageSize > _options.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                $"Page size must be between 1 and {_options.MaxPageSize}.");
        }
    }

    private static void ValidateState(QueryState<TEntity> state)
    {
        if (state.IndexName is not null && state.ConsistentRead)
        {
            throw new InvalidOperationException(
                "DynamoDB global secondary indexes do not support consistent reads.");
        }
    }

    private void ValidatePlan(QueryState<TEntity> state, QueryPlan plan)
    {
        if (state.OrderProperty is null)
        {
            return;
        }

        if (!plan.IsQuery)
        {
            throw new InvalidOperationException(
                "DynamoDB ordering requires a partition-key query; scans cannot be ordered.");
        }

        var sortKey = _metadata.GetKeySchema(state.IndexName).SortKey;
        if (sortKey is null || sortKey.Property != state.OrderProperty.Property)
        {
            throw new InvalidOperationException(
                "DynamoDB can only order by the selected table or index sort key.");
        }
    }

    private static void ValidateOptions(DynamoDbRepositoryOptions options)
    {
        if (options.DefaultFetchSize < 1)
        {
            throw new InvalidOperationException("DefaultFetchSize must be greater than zero.");
        }

        if (options.MaxPageSize < 1)
        {
            throw new InvalidOperationException("MaxPageSize must be greater than zero.");
        }
    }
}