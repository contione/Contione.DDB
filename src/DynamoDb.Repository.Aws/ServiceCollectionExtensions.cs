using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamoDb.Repository.Aws;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamoDbRepositories(
        this IServiceCollection services,
        Action<DynamoDbRepositoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<DynamoDbRepositoryOptions>()
            .Validate(static value => value.DefaultFetchSize > 0 && value.MaxPageSize > 0 &&
                value.MaxRequestsPerOperation > 0 && value.MaxEvaluatedItems > 0,
                "Fetch size, page size and execution budgets must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(typeof(IDynamoDbRepository<>), typeof(AwsDynamoDbRepository<>));
        return services;
    }
}
