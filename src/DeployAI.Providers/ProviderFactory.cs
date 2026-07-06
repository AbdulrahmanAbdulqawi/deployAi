using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Providers;

public sealed class ProviderFactory : IProviderFactory
{
    private readonly IReadOnlyDictionary<string, IDeploymentProvider> _providers;

    public ProviderFactory(IEnumerable<IDeploymentProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IDeploymentProvider GetProvider(string providerName)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new KeyNotFoundException($"Provider '{providerName}' is not registered.");
        }

        return provider;
    }

    public IReadOnlyList<ProviderInfo> GetAvailableProviders() =>
        _providers.Values
            .Select(p => new ProviderInfo(p.ProviderName, p.DisplayName, p.ApiStyle))
            .ToList();
}

public sealed class ProviderManagementFactory : IProviderManagementFactory
{
    private readonly IReadOnlyDictionary<string, IProviderManagement> _providers;

    public ProviderManagementFactory(IEnumerable<IProviderManagement> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderManagement GetManagement(string providerName)
    {
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new KeyNotFoundException($"Management for provider '{providerName}' is not registered.");
        }

        return provider;
    }
}

public sealed class ProviderDatabaseProvisioningFactory : IProviderDatabaseProvisioningFactory
{
    private readonly IReadOnlyDictionary<string, IProviderDatabaseProvisioning> _providers;

    public ProviderDatabaseProvisioningFactory(IEnumerable<IProviderDatabaseProvisioning> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderDatabaseProvisioning? GetProvisioning(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class ProviderServiceOperationsFactory : IProviderServiceOperationsFactory
{
    private readonly IReadOnlyDictionary<string, IProviderServiceOperations> _providers;

    public ProviderServiceOperationsFactory(IEnumerable<IProviderServiceOperations> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderServiceOperations? GetServiceOperations(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class ProviderDataServiceInspectionFactory : IProviderDataServiceInspectionFactory
{
    private readonly IReadOnlyDictionary<string, IProviderDataServiceInspection> _providers;

    public ProviderDataServiceInspectionFactory(IEnumerable<IProviderDataServiceInspection> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderDataServiceInspection? GetInspection(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public static class ProviderDependencyInjection
{
    public static IServiceCollection AddDeploymentProviders(this IServiceCollection services)
    {
        services.AddHttpClient<Vercel.VercelProvider>();
        services.AddSingleton<Vercel.VercelProvider>();
        services.AddSingleton<IDeploymentProvider>(sp => sp.GetRequiredService<Vercel.VercelProvider>());
        services.AddSingleton<IProviderManagement>(sp => sp.GetRequiredService<Vercel.VercelProvider>());
        services.AddHttpClient(RailwayClient.ClientName, client =>
        {
            client.BaseAddress = new Uri(Railway.RailwayGraphQlClientFactory.GraphQlEndpoint);
        });
        services.AddSingleton<Railway.RailwayGraphQlClientFactory>();
        services.AddSingleton<Railway.RailwayProvider>();
        services.AddSingleton<IDeploymentProvider>(sp => sp.GetRequiredService<Railway.RailwayProvider>());
        services.AddSingleton<IProviderManagement>(sp => sp.GetRequiredService<Railway.RailwayProvider>());
        services.AddSingleton<IProviderDatabaseProvisioning>(sp => sp.GetRequiredService<Railway.RailwayProvider>());
        services.AddSingleton<IProviderServiceOperations>(sp => sp.GetRequiredService<Railway.RailwayProvider>());
        services.AddSingleton<IProviderDataServiceInspection>(sp => sp.GetRequiredService<Railway.RailwayProvider>());
        services.AddSingleton<IProviderDatabaseProvisioningFactory, ProviderDatabaseProvisioningFactory>();
        services.AddSingleton<IProviderServiceOperationsFactory, ProviderServiceOperationsFactory>();
        services.AddSingleton<IProviderDataServiceInspectionFactory, ProviderDataServiceInspectionFactory>();
        services.AddSingleton<IProviderFactory, ProviderFactory>();
        services.AddSingleton<IProviderManagementFactory, ProviderManagementFactory>();
        return services;
    }
}
