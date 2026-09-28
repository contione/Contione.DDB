using Accounts.Application;
using Accounts.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddAccountsInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

var accounts = app.MapGroup("/accounts");

accounts.MapPost("/", async (
    CreateAccountCommand command,
    IAccountService service,
    CancellationToken cancellationToken) =>
{
    var account = await service.CreateAsync(command, cancellationToken);
    return Results.Created($"/accounts/{account.TenantId}/{account.AccountId}", account);
});

accounts.MapGet("/{tenantId}/{accountId}", async (
    string tenantId,
    string accountId,
    IAccountService service,
    CancellationToken cancellationToken) =>
{
    var account = await service.GetAsync(tenantId, accountId, cancellationToken);
    return account is null ? Results.NotFound() : Results.Ok(account);
});

accounts.MapGet("/", async (
    string tenantId,
    string? name,
    int? status,
    int pageSize,
    string? continuationToken,
    IAccountService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.SearchAsync(
        new AccountSearch(tenantId, name, status, pageSize, continuationToken),
        cancellationToken);
    return Results.Ok(result);
});

accounts.MapDelete("/{tenantId}/{accountId}", async (
    string tenantId,
    string accountId,
    IAccountService service,
    CancellationToken cancellationToken) =>
{
    await service.DeleteAsync(tenantId, accountId, cancellationToken);
    return Results.NoContent();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
