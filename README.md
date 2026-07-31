# .NET 10 DynamoDB Fluent Repository

这是一个可运行、可打包的 .NET 10 小型解决方案。它用洋葱架构组织账户示例，并提供类似 EF Core 的不可变链式 DynamoDB 仓储 API。

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
var all = await repository.Query.Where(x => x.Status == 0).ToListAsync(cancellationToken);
var first = await repository.Query.Where(x => x.Name == "Ada").FirstOrDefaultAsync(cancellationToken);
var exists = await repository.Query.Where(x => x.Status == 0).AnyAsync(cancellationToken);
var count = await repository.Query.Where(x => x.Status == 0).CountAsync(cancellationToken);
```

`ToListAsync` 对应问题中的 `ToList`；分页可用 `ToPageAsync`，也提供语义别名 `PageListAsync`。仓储只提供异步终结方法，避免在 ASP.NET Core 中阻塞线程。

## DynamoDB 执行语义

- 当前表或索引的分区键出现等值条件时自动使用 `Query`。
- 没有分区键等值条件时使用 `Scan`。例如只按 `Name` 查询无法由 DynamoDB 自动变成高效键查询。
- 排序只允许用于 `Query`，且只能按当前表或索引的排序键。
- 多个 `Where` 自动以 `AND` 合并；键条件会从普通过滤条件中拆出。
- 支持 `==`、`!=`、`>`、`>=`、`<`、`<=`、`&&`、`||`、`!`、`StartsWith`、`Contains` 和常量集合 `Contains`。
- 分页会在过滤导致空结果时继续读取，尽量填满请求页；令牌保存完整 `LastEvaluatedKey` 类型。
- 查询对象不可变，可以从同一个基础查询安全派生多个查询。

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

还可使用 `DynamoDbIndexPartitionKey`、`DynamoDbIndexSortKey` 和 `DynamoDbIgnore`。元数据和属性 getter 会缓存，查询翻译不执行含实体参数的表达式。

## 注册

```csharp
services.AddDefaultAWSOptions(configuration.GetAWSOptions());
services.AddAWSService<IAmazonDynamoDB>();
services.AddDynamoDbRepositories(options =>
{
    options.TableNamePrefix = "dev-";
    options.DefaultFetchSize = 100;
    options.MaxPageSize = 1_000;
});
```

AWS 凭证使用标准 AWS SDK 凭证链，不要写入配置文件或源码。

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

## 打包

```powershell
dotnet pack src/DynamoDb.Repository/DynamoDb.Repository.csproj -c Release -o artifacts
dotnet pack src/DynamoDb.Repository.Aws/DynamoDb.Repository.Aws.csproj -c Release -o artifacts
```

分页令牌采用 JSON + Base64Url 编码，能稳定往返 AWS SDK v4 的 `AttributeValue`。Base64 不提供签名；若 API 面向不可信客户端并要求拒绝令牌篡改，可在 `ContinuationTokenCodec` 外增加 HMAC 保护。
