using Accounts.Application;
using Amazon.DynamoDBv2;
using DynamoDb.Repository.Aws;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Accounts.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAccountsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonDynamoDB>();
        services.AddDynamoDbRepositories(options =>
            configuration.GetSection(DynamoDbRepositoryOptions.SectionName).Bind(options));
        services.AddScoped<IAccountService, AccountService>();
        return services;
    }
}