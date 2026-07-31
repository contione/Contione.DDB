using Accounts.Domain;
using DynamoDb.Repository;

namespace Accounts.Application;

public sealed class AccountService(IDynamoDbRepository<Account> accountRepository) : IAccountService
{
    public async Task<Account> CreateAsync(
        CreateAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);

        var account = new Account
        {
            TenantId = command.TenantId,
            AccountId = Guid.CreateVersion7().ToString("N"),
            Name = command.Name,
            Email = command.Email,
            Status = command.Status,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await accountRepository.PutAsync(account, cancellationToken).ConfigureAwait(false);
        return account;
    }

    public Task<Account?> GetAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken = default) =>
        accountRepository.GetAsync(tenantId, accountId, cancellationToken: cancellationToken);

    public Task<PageResult<Account>> SearchAsync(
        AccountSearch search,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search.TenantId);

        return accountRepository.Query
            .Where(account => account.TenantId == search.TenantId)
            .WhereIf(!string.IsNullOrWhiteSpace(search.Name), account => account.Name.Contains(search.Name!))
            .WhereIf(search.Status.HasValue, account => account.Status == search.Status!.Value)
            .OrderByDescending(account => account.AccountId)
            .ToPageAsync(search.PageSize, search.ContinuationToken, cancellationToken);
    }

    public Task DeleteAsync(
        string tenantId,
        string accountId,
        CancellationToken cancellationToken = default) =>
        accountRepository.DeleteAsync(tenantId, accountId, cancellationToken);
}