# .NET 10 DynamoDB Fluent Repository

面向 .NET 10 / AWS SDK v4 的 DynamoDB 通用仓储。提供不可变查询、条件写入、局部更新、映射与分页；账户 API 是用法示例。

## 2.0 升级说明

- 默认拒绝全表/索引扫描，需要在查询上显式调用 `AllowScan()`，或配置 `AllowScan = true`。
- 每次终结操作默认最多发出 100 次 SDK 请求、评估 10,000 条记录；达到上限且操作仍未完成时抛 `DynamoDbQueryLimitExceededException`，不会把不完整结果伪装成完整结果。
- `Take` 用于 `ToListAsync` / `CountAsync`；与 `ToPageAsync` 同时使用会明确报错，分页请使用 `pageSize`。
- 新分页令牌绑定表、索引、查询参数与排序方向。升级前的令牌需要重新从第一页获取。
- 主键参数必须匹配实体声明的 CLR 类型；空键、无效键类型和重复映射在发送请求前被拒绝。
- 新增仓储接口成员，自定义实现需要同步更新。

## 项目结构

```text
src/
  DynamoDb.Repository/          仓储接口、分页模型、映射特性
  DynamoDb.Repository.Aws/      AWS SDK v4 实现、表达式翻译、分页令牌
  Accounts.Domain/              不依赖 AWS SDK 的领域实体
  Accounts.Application/         用例和链式查询示例
  Accounts.Infrastructure/      AWS 与仓储依赖注入
  Accounts.Api/                 Minimal API
tests/
  DynamoDb.Repository.Tests/    表达式、映射、分页、CRUD 和校验测试
infra/
  template.yaml                 DynamoDB CloudFormation 模板
```

依赖方向为 `Api -> Infrastructure -> Application -> Domain`，仓储抽象与 AWS 实现分开。领域层只使用仓储包自己的映射特性，不依赖 AWS SDK。

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
[DynamoDbTable("accounts")]
public sealed class Account
{
    [DynamoDbPartitionKey]
    [DynamoDbProperty("tenant_id")]
    public required string TenantId { get; init; }

    [DynamoDbSortKey]
    [DynamoDbProperty("account_id")]
    public required string AccountId { get; init; }
}
```

还可使用 `DynamoDbIndexPartitionKey`、`DynamoDbIndexSortKey` 和 `DynamoDbIgnore`。当前索引特性表示 GSI，不支持 GSI 强一致读取。实体元数据和属性 getter 会缓存，查询翻译不执行含实体参数的表达式。

枚举统一存储为字符串名称，查询比较和读回使用同一表示；重命名枚举成员需要数据迁移。可空索引键为 null 时省略该属性，以支持稀疏索引。支持 `JsonPropertyName`；忽略数据库属性请使用 `DynamoDbIgnore`。公开索引器不参与映射。

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
dotnet pack src/DynamoDb.Repository.Aws/DynamoDb.Repository.Aws.csproj -c Release -o artifacts
```

分页令牌采用带版本和查询上下文指纹的 JSON + Base64Url，保留字符串、数字、二进制键类型。上下文校验用于防止误用游标，不是密码学签名，也不是授权机制。需要拒绝客户端篡改的 API 应在边界对整个令牌签名或加密；不应将令牌内容作为租户或权限依据。

本库没有宣称支持完整 LINQ、事务、批量写入、任意投影或 Native AOT。生产发布仍应在目标 AWS 账户验证 IAM、吞吐与重试行为，并用业务数据进行负载测试；DynamoDB Local 不模拟这些托管服务特性。
