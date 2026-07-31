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

        var options = services.AddOptions<DynamoDbRepositoryOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(typeof(IDynamoDbRepository<>), typeof(AwsDynamoDbRepository<>));
        return services;
    }
}