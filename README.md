# .NET 10 DynamoDB Fluent Repository

面向 .NET 10 / AWS SDK v3 的 DynamoDB 通用仓储。一个类库项目、一个 NuGet 包；使用官方 DataModel 映射，提供不可变查询、条件写入、局部更新与分页。账户 API 是用法示例。

## 3.1 升级说明

当前锁定 `AWSSDK.DynamoDBv2 3.7.406.23` 和 `AWSSDK.Extensions.NETCore.Setup 3.7.400`。AWS SDK v3 没有 v4 的 `DynamoDBContextBuilder`，仓储使用官方的 `DynamoDBContext(IAmazonDynamoDB, DynamoDBContextConfig)` 构造方式；官方 DataModel Attribute、`IPropertyConverter` 和查询能力保持可用。

- 合并为 `DynamoDb.Repository` 一个项目和命名空间，仅发布 `Professional.DynamoDb.Repository` 包。实现类型为 `DynamoDbRepository<TEntity>`。
- 删除自定义映射 Attribute，实体直接使用 `Amazon.DynamoDBv2.DataModel` 下的官方特性。
- 实体读写交给 `DynamoDBContext`，默认使用 SDK V2 转换规则；查询和局部更新也遵循属性上的 `IPropertyConverter`。
- 自定义实体需要同步迁移注解；直接依赖实体映射的项目引用 `AWSSDK.DynamoDBv2`。集合按 SDK 支持的 `List<T>`、数组、`HashSet<T>` 等声明，不再额外实现 `IReadOnlyList<T>` 映射。

- 默认拒绝全表/索引扫描，需要在查询上显式调用 `AllowScan()`，或配置 `AllowScan = true`。
- 每次终结操作默认最多发出 100 次 SDK 请求、评估 10,000 条记录；达到上限且操作仍未完成时抛 `DynamoDbQueryLimitExceededException`，不会把不完整结果伪装成完整结果。
- `Take` 用于 `ToListAsync` / `CountAsync`；与 `ToPageAsync` 同时使用会明确报错，分页请使用 `pageSize`。
- 新分页令牌绑定表、索引、查询参数与排序方向。升级前的令牌需要重新从第一页获取。
- 主键参数必须匹配实体声明的 CLR 类型；空键、无效键类型和重复映射在发送请求前被拒绝。
- 新增仓储接口成员，自定义实现需要同步更新。

## 项目结构

```text
src/
  DynamoDb.Repository/          仓储接口与实现、查询、更新、分页
  Accounts.Domain/              使用官方 DataModel 特性的示例实体
  Accounts.Application/         用例和链式查询示例
  Accounts.Infrastructure/      AWS 与仓储依赖注入
  Accounts.Api/                 Minimal API
tests/
  DynamoDb.Repository.Tests/    表达式、映射、分页、CRUD 和校验测试
infra/
  template.yaml                 DynamoDB CloudFormation 模板
```

账户示例依赖方向为 `Api -> Infrastructure -> Application -> Domain`。仓储自身不再拆分抽象包与实现包，也不维护独立映射协议。

## 链式查询

```csharp
var page = await accountRepository.Query
    .Where(account => account.TenantId == tenantId)
    .WhereIf(!string.IsNullOrWhiteSpace(name),
        account => account.Name.Contains(name!))
    .WhereIf(status.HasValue,
        account => account.Status == status!.Value)
    .OrderByDescending(account => account.AccountId)
    .ToPageAsync(pageSize, continuationToken, cancellationToken);
```

也支持：

```csharp
var all = await repository.Query.AllowScan().Where(x => x.Status == 0).ToListAsync(cancellationToken);
var first = await repository.Query.AllowScan().Where(x => x.Name == "Ada").FirstOrDefaultAsync(cancellationToken);
var exists = await repository.Query.AllowScan().Where(x => x.Status == 0).AnyAsync(cancellationToken);
var count = await repository.Query.AllowScan().Where(x => x.Status == 0).CountAsync(cancellationToken);
```

`ToListAsync` 对应问题中的 `ToList`；分页可用 `ToPageAsync`，也提供语义别名 `PageListAsync`。仓储只提供异步终结方法，避免在 ASP.NET Core 中阻塞线程。

## DynamoDB 执行语义

- 当前表或索引的分区键出现等值条件时自动使用 `Query`。
- 没有分区键等值条件时需要 `Scan`，默认拒绝执行。例如只按 `Name` 查询无法由 DynamoDB 自动变成高效键查询。
- 排序只允许用于 `Query`，且只能按当前表或索引的排序键。
- 多个 `Where` 自动以 `AND` 合并；键条件会从普通过滤条件中拆出。
- 支持 `==`、`!=`、`>`、`>=`、`<`、`<=`、`&&`、`||`、`!`、`StartsWith`、`Contains` 和常量集合 `Contains`。
- 分页会在过滤导致空结果时继续读取，尽量填满请求页；令牌保存完整 `LastEvaluatedKey` 类型。
- 查询对象不可变，可以从同一个基础查询安全派生多个查询。
- 排序键的 `>= lower && <= upper` 合并为 `BETWEEN`；不能表示的复合键条件提前报错，不会偷偷改成扫描。
- `AnyAsync` / `FirstOrDefaultAsync` 按读取批量查找，不会因为仅需一个结果而每次只检查一条数据。
- `DefaultFetchSize` 控制单次读取量，`MaxPageSize` 仅限制公开分页大小；`ToListAsync` 在一次操作中复用翻译结果和原始游标。
- `PageResult` 包含 `ScannedCount` 和 `RequestCount`，可记录过滤效率与请求次数。SDK 自动重试不计入 `RequestCount`。

DynamoDB 不支持任意字段服务器端排序、关系导航、联表或完整 LINQ。需要高性能查询的业务条件应设计对应 GSI，并通过 `UseIndex("index-name")` 使用。

## 实体映射

```csharp
using Amazon.DynamoDBv2.DataModel;

[DynamoDBTable("accounts")]
public sealed class Account
{
    [DynamoDBHashKey("tenant_id")]
    public required string TenantId { get; init; }

    [DynamoDBRangeKey("account_id")]
    public required string AccountId { get; init; }
}
```

属性重命名使用 `DynamoDBProperty`，忽略属性使用 `DynamoDBIgnore`。GSI 使用 `DynamoDBGlobalSecondaryIndexHashKey` / `DynamoDBGlobalSecondaryIndexRangeKey`；LSI 使用 `DynamoDBLocalSecondaryIndexRangeKey`，允许强一致读取。索引特性可声明多个索引名。

SDK 默认将枚举写为数字。已有字符串枚举数据应使用官方扩展点 `[DynamoDBProperty(typeof(YourEnumConverter))]` 明确保持字符串格式，或者先迁移数据；仅能读回旧值并不意味着数字查询会匹配旧字符串值。查询、条件写入和局部更新使用同一个属性转换规则。

`DateTimeOffset` 由示例中的 `UtcDateTimeOffsetConverter` 保持原有 UTC ISO 8601 格式；它实现 SDK 的 `IPropertyConverter`，没有另建转换协议。`DateTime` 可使用官方 `StoreAsEpochLong`，不支持已废弃的 `StoreAsEpoch`。可空索引键省略 null，以支持稀疏索引。JSON 特性不参与数据库映射。

实体序列化调用官方 `ToDocument` / `FromDocument` 和目标表转换，不再经过 JSON 中转。内部只保留查询规划所需的键、索引和属性元数据；禁用隐式 `DescribeTable`，依赖显式注解。

仓储保留整条替换和显式条件写入语义，不会模拟 SDK `SaveAsync` 的自动版本递增。需要 `[DynamoDBVersion]` 自动乐观锁时直接使用 `DynamoDBContext.SaveAsync`；仓储会明确拒绝该特性，避免静默忽略版本检查。

## 条件写入与局部更新

`PutAsync` 明确表示创建或整条替换。仅创建请使用 `CreateAsync`；条件不成立时抛出与 AWS SDK 解耦的 `DynamoDbConditionFailedException`，调用方可以映射为业务冲突。

```csharp
await repository.CreateAsync(entity, cancellationToken);

// 实体声明 Version 属性，调用方持有之前读取的 expectedVersion。
var update = new DynamoDbUpdate<Order>()
    .Set(x => x.Status, OrderStatus.Paid)
    .Set(x => x.Version, expectedVersion + 1);

await repository.UpdateAsync(tenantId, orderId, update,
    x => x.Version == expectedVersion, cancellationToken);
```

`UpdateAsync` 默认要求记录存在；主键不能修改，同一属性不能重复修改。`Remove(x => x.OptionalProperty)` 删除属性；清空稀疏索引键也应使用 `Remove`。支持带条件的 `PutAsync`、`DeleteAsync`。版本推进由调用方显式设置，条件检查与更新由 DynamoDB 原子执行。

条件失败不应盲目重试；读取最新状态后再决定业务行为。超时或取消不能证明写入未发生，涉及订单等业务时仍需应用层幂等标识。服务限流和瞬态错误交由 AWS SDK 的重试策略处理。

## 批量写入与删除

```csharp
await repository.BatchWriteAsync(
    putItems: accountsToPut,
    deleteItems: accountsToDelete,
    cancellationToken: cancellationToken);
```

两个集合都可省略，也可为空；删除实体只使用其主键。内部复用官方 `DynamoDBContext.CreateBatchWrite<TEntity>()`，使用仓储配置的实际表名（含前缀），由 SDK 按每批最多 25 个操作执行并处理返回的未处理项。取消令牌会传递给 SDK。

Put 是整条替换，不支持局部更新或条件写入；同一主键不要重复出现在同一批请求中。整批不是事务，失败或取消时可能已有部分操作成功，不会自动回滚。该方法的批处理由 SDK 管理，不受仅用于读取的 `MaxRequestsPerOperation` / `MaxEvaluatedItems` 限制；大量数据建议由调用方分段传入并设置超时。

## 注册

```csharp
services.AddDefaultAWSOptions(configuration.GetAWSOptions());
services.AddAWSService<IAmazonDynamoDB>();
services.AddDynamoDbRepositories(options =>
{
    options.TableNamePrefix = "dev-";
    options.DefaultFetchSize = 100;
    options.MaxPageSize = 1_000;
    options.MaxRequestsPerOperation = 100;
    options.MaxEvaluatedItems = 10_000;
});
```

选项在启动时验证，仓储使用配置快照。所有异步 API 传递取消令牌，调用方应设置符合业务 SLA 的超时。AWS 凭证使用标准 AWS SDK 凭证链，不要写入配置文件或源码。

## 创建表和运行

```powershell
aws cloudformation deploy `
  --template-file infra/template.yaml `
  --stack-name ddb-repository-dev `
  --parameter-overrides Environment=dev

dotnet restore DynamoDbRepository.slnx
dotnet test DynamoDbRepository.slnx --configuration Release
dotnet run --project src/Accounts.Api
```

开发环境 OpenAPI 地址为 `http://localhost:5188/openapi/v1.json`。运行 API 前需要有效 AWS 凭证和 `ap-southeast-1` 区域中的 `dev-accounts` 表。

账户 API 未实现身份认证与租户授权，不应直接作为生产业务接口部署；生产服务应从认证上下文获取租户，并设置最小权限 IAM、超时和监控。类库不会代替业务授权。

## 验证

```powershell
dotnet restore DynamoDbRepository.slnx --locked-mode
dotnet build DynamoDbRepository.slnx -c Release --no-restore
dotnet test DynamoDbRepository.slnx -c Release --no-build
```

单元测试覆盖表达式、映射、游标、分页、资源预算与条件写入。DynamoDB Local 集成测试验证服务真实请求语法、范围查询、分页、稀疏索引及并发条件；未配置端点时明确显示为跳过。

```powershell
docker run --rm -p 8000:8000 amazon/dynamodb-local:latest
# 在另一个终端运行：
$env:DYNAMODB_LOCAL_ENDPOINT = "http://localhost:8000"
dotnet test DynamoDbRepository.slnx -c Release
```

集成测试只接受回环地址，使用临时表和虚拟凭证，测试结束后删除临时表。GitHub Actions 自动运行构建、含 DynamoDB Local 的测试和 NuGet 打包。依赖通过 `packages.lock.json` 锁定。

## 打包

```powershell
dotnet pack src/DynamoDb.Repository/DynamoDb.Repository.csproj -c Release -o artifacts
```

分页令牌采用带版本和查询上下文指纹的 JSON + Base64Url，保留字符串、数字、二进制键类型。上下文校验用于防止误用游标，不是密码学签名，也不是授权机制。需要拒绝客户端篡改的 API 应在边界对整个令牌签名或加密；不应将令牌内容作为租户或权限依据。

本库没有宣称支持完整 LINQ、事务、批量局部更新、任意投影或 Native AOT。生产发布仍应在目标 AWS 账户验证 IAM、吞吐与重试行为，并用业务数据进行负载测试；DynamoDB Local 不模拟这些托管服务特性。
