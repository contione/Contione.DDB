using Accounts.Domain;
using DynamoDb.Repository;

namespace Accounts.Application;

public sealed record CreateAccountCommand(
    string TenantId,
    string Name,
    string? Email,
    int Status = 0);

public sealed record AccountSearch(
    string TenantId,
    string? Name,
    int? Status,
    int PageSize = 20,
    string? ContinuationToken = null);

public interface IAccountService
{
    Task<Account> CreateAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken = default);

    Task<Account?> GetAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken = default);

    Task<PageResult<Account>> SearchAsync(
        AccountSearch search,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken = default);
}