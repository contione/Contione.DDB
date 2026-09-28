using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;

namespace DynamoDb.Repository;

public sealed partial class DynamoDbRepository<TEntity> : IDynamoDbRepository<TEntity>, IDisposable
    where TEntity : class
{
    private readonly IAmazonDynamoDB _client;
    private readonly DynamoDbRepositoryOptions _options;
    private readonly EntityMetadata _metadata = EntityMetadata.For<TEntity>();
    private readonly EntityMapper<TEntity> _mapper;
    private readonly string _tableName;

    public DynamoDbRepository(IAmazonDynamoDB client, IOptions<DynamoDbRepositoryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _mapper = new(client);
        var configured = options.Value;
        if (!ValidOptions(configured))
        {
            throw new ArgumentException("Fetch size, page size, request budget and evaluated-item budget must be positive.", nameof(options));
        }

        // A singleton repository uses one stable configuration throughout an operation.
        _options = new DynamoDbRepositoryOptions
        {
            TableNamePrefix = configured.TableNamePrefix,
            DefaultFetchSize = configured.DefaultFetchSize,
            MaxPageSize = configured.MaxPageSize,
            AllowScan = configured.AllowScan,
            MaxRequestsPerOperation = configured.MaxRequestsPerOperation,
            MaxEvaluatedItems = configured.MaxEvaluatedItems,
        };
        _tableName = string.Concat(_options.TableNamePrefix, _metadata.TableName);
    }

    public IDynamoDbQuery<TEntity> Query => new DynamoDbQuery<TEntity>(this);

    public void Dispose() => _mapper.Dispose();

    internal static bool ValidOptions(DynamoDbRepositoryOptions options) =>
        options.DefaultFetchSize > 0 && options.MaxPageSize > 0 &&
        options.MaxRequestsPerOperation > 0 && options.MaxEvaluatedItems > 0;

    public async Task<TEntity?> GetAsync(
        object partitionKey,
        object? sortKey = null,
        bool consistentRead = false,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = CreateKey(partitionKey, sortKey),
            ConsistentRead = consistentRead,
        }, cancellationToken).ConfigureAwait(false);
        return response.Item is { Count: > 0 } ? _mapper.FromMap(response.Item) : null;
    }

    internal async Task<PageResult<TEntity>> ExecutePageAsync(
        QueryState<TEntity> state, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > _options.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {_options.MaxPageSize}.");
        }
        if (state.Take is not null)
        {
            throw new InvalidOperationException("Take cannot be combined with pagination. Use pageSize to control each page, or Take with ToListAsync.");
        }

        var plan = CreatePlan(state);
        var context = CreateTokenContext(state, plan);
        var lastKey = ContinuationTokenCodec.Decode(continuationToken, context);
        var items = new List<TEntity>(Math.Min(pageSize, 1_024));
        var budget = new QueryBudget(_options);
        do
        {
            // A page must not fetch past its returned items, or its cursor would skip results.
            var response = await ReadAsync(state, plan, pageSize - items.Count, lastKey, false, budget, cancellationToken).ConfigureAwait(false);
            AddItems(response.Items, items);
            lastKey = response.LastKey;
        }
        while (items.Count < pageSize && lastKey is { Count: > 0 });

        return new PageResult<TEntity>(items.AsReadOnly(), ContinuationTokenCodec.Encode(lastKey, context), budget.EvaluatedItems)
        {
            RequestCount = budget.Requests,
        };
    }

    internal async Task<IReadOnlyList<TEntity>> ExecuteListAsync(QueryState<TEntity> state, CancellationToken cancellationToken)
    {
        var plan = CreatePlan(state);
        var capacity = state.Take ?? int.MaxValue;
        var items = new List<TEntity>(Math.Min(capacity, Math.Min(_options.DefaultFetchSize, 1_024)));
        var budget = new QueryBudget(_options);
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await ReadAsync(state, plan, capacity - items.Count, lastKey, false, budget, cancellationToken).ConfigureAwait(false);
            AddItems(response.Items, items);
            lastKey = response.LastKey;
        }
        while (items.Count < capacity && lastKey is { Count: > 0 });
        return items.AsReadOnly();
    }

    internal async Task<TEntity?> ExecuteFirstAsync(QueryState<TEntity> state, CancellationToken cancellationToken)
    {
        var plan = CreatePlan(state);
        var budget = new QueryBudget(_options);
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await ReadAsync(state, plan, _options.DefaultFetchSize, lastKey, false, budget, cancellationToken).ConfigureAwait(false);
            if (response.Items is { Count: > 0 }) return _mapper.FromMap(response.Items[0]);
            lastKey = response.LastKey;
        }
        while (lastKey is { Count: > 0 });
        return null;
    }

    internal async Task<int> ExecuteCountAsync(QueryState<TEntity> state, bool existsOnly, CancellationToken cancellationToken)
    {
        var plan = CreatePlan(state);
        var budget = new QueryBudget(_options);
        var maximum = existsOnly ? int.MaxValue : state.Take ?? int.MaxValue;
        var total = 0;
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await ReadAsync(state, plan, maximum - total, lastKey, true, budget, cancellationToken).ConfigureAwait(false);
            total = checked(total + response.Count);
            if (existsOnly && total > 0) return 1;
            lastKey = response.LastKey;
        }
        while (total < maximum && lastKey is { Count: > 0 });
        return total;
    }

    private async Task<ReadResult> ReadAsync(
        QueryState<TEntity> state, QueryPlan plan, int limit, Dictionary<string, AttributeValue>? lastKey,
        bool countOnly, QueryBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limit = budget.BeginRequest(Math.Min(limit, _options.DefaultFetchSize));
        ReadResult result;
        if (plan.IsQuery)
        {
            var response = await _client.QueryAsync(new QueryRequest
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
                Select = countOnly ? Select.COUNT : null,
            }, cancellationToken).ConfigureAwait(false);
            result = new(response.Items, response.LastEvaluatedKey, response.Count, response.ScannedCount);
        }
        else
        {
            var response = await _client.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                IndexName = state.IndexName,
                FilterExpression = plan.FilterExpression,
                ExpressionAttributeNames = plan.Names.Count == 0 ? null : plan.Names,
                ExpressionAttributeValues = plan.Values.Count == 0 ? null : plan.Values,
                ExclusiveStartKey = lastKey,
                Limit = limit,
                ConsistentRead = state.ConsistentRead,
                Select = countOnly ? Select.COUNT : null,
            }, cancellationToken).ConfigureAwait(false);
            result = new(response.Items, response.LastEvaluatedKey, response.Count, response.ScannedCount);
        }
        budget.Record(result.ScannedCount);
        return result;
    }

    internal PropertyMetadata GetOrderProperty(LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }
        return body is MemberExpression member && member.Expression == selector.Parameters[0]
            ? _metadata.GetProperty(member.Member)
            : throw new ArgumentException("Selector must be a mapped top-level property.", nameof(selector));
    }

    private QueryPlan CreatePlan(QueryState<TEntity> state)
    {
        if (_metadata.GetKeySchema(state.IndexName).IsGlobalIndex && state.ConsistentRead)
        {
            throw new InvalidOperationException("Global secondary indexes do not support consistent reads.");
        }
        var plan = QueryPlanner.Create(state.Predicates, state.IndexName, _mapper.Context);
        if (!plan.IsQuery && !(state.AllowScan ?? _options.AllowScan))
        {
            throw new InvalidOperationException("This query requires a scan. Add a partition-key equality or explicitly call AllowScan().");
        }
        if (state.OrderProperty is not null)
        {
            if (!plan.IsQuery) throw new InvalidOperationException("DynamoDB scans cannot be ordered.");
            if (_metadata.GetKeySchema(state.IndexName).SortKey?.Property != state.OrderProperty.Property)
            {
                throw new InvalidOperationException("DynamoDB can only order by the selected table or index sort key.");
            }
        }
        return plan;
    }

    private string CreateTokenContext(QueryState<TEntity> state, QueryPlan plan)
    {
        var values = plan.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new { pair.Key, Value = AttributeValueFingerprint(pair.Value) });
        var json = JsonSerializer.Serialize(new
        {
            Table = _tableName,
            state.IndexName,
            state.Descending,
            state.ConsistentRead,
            plan.KeyExpression,
            plan.FilterExpression,
            Names = plan.Names.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            Values = values,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static object AttributeValueFingerprint(AttributeValue value) => new
    {
        value.S,
        value.N,
        value.BOOL,
        value.NULL,
        B = value.B is null ? null : Convert.ToBase64String(value.B.ToArray()),
        value.SS,
        value.NS,
        BS = value.BS?.Select(static bytes => Convert.ToBase64String(bytes.ToArray())),
        L = value.L?.Select(AttributeValueFingerprint),
        M = value.M?.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new { pair.Key, Value = AttributeValueFingerprint(pair.Value) }),
    };

    private Dictionary<string, AttributeValue> CreateKey(object partitionKey, object? sortKey)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [_metadata.PrimaryKey.PartitionKey.AttributeName] = ConvertKey(partitionKey, _metadata.PrimaryKey.PartitionKey),
        };
        if (_metadata.PrimaryKey.SortKey is { } sortProperty)
        {
            ArgumentNullException.ThrowIfNull(sortKey);
            key[sortProperty.AttributeName] = ConvertKey(sortKey, sortProperty);
        }
        else if (sortKey is not null)
        {
            throw new ArgumentException("This entity does not define a sort key.", nameof(sortKey));
        }
        return key;
    }

    private AttributeValue ConvertKey(object value, PropertyMetadata property)
        => AttributeValueConverter.FromKey(value, property, _metadata.Conversion, _mapper.Context);

    private void AddItems(List<Dictionary<string, AttributeValue>>? source, ICollection<TEntity> destination)
    {
        if (source is null) return;
        foreach (var item in source) destination.Add(_mapper.FromMap(item));
    }

    private sealed record ReadResult(
        List<Dictionary<string, AttributeValue>>? Items, Dictionary<string, AttributeValue>? LastKey,
        int Count, int ScannedCount);

    private sealed class QueryBudget(DynamoDbRepositoryOptions options)
    {
        public int Requests { get; private set; }
        public int EvaluatedItems { get; private set; }

        public int BeginRequest(int limit)
        {
            if (Requests >= options.MaxRequestsPerOperation || EvaluatedItems >= options.MaxEvaluatedItems)
            {
                throw new DynamoDbQueryLimitExceededException(Requests, EvaluatedItems);
            }
            Requests++;
            return Math.Min(limit, options.MaxEvaluatedItems - EvaluatedItems);
        }

        public void Record(int count) => EvaluatedItems = checked(EvaluatedItems + count);
    }
}
