using DeployAI.Core.Domains;
using DeployAI.Core.Providers;
using DeployAI.Providers.Railway.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Providers;

// The factories below (ProviderFactory, ProviderManagementFactory, and the optional-capability
// factories) all follow the same pattern: DI injects every registered provider implementing a
// given interface, keyed by ProviderName, and GetX(name)/TryGetX(name) looks it up - throwing for
// required capabilities (IDeploymentProvider, IProviderManagement) and returning null for optional
// ones a given provider might not implement. See ProviderDependencyInjection.AddDeploymentProviders
// below for the actual provider registrations.

/// <summary>Resolves the registered <see cref="IDeploymentProvider"/> for a given provider name.</summary>
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

public sealed class ProviderApplicationUrlResolverFactory : IProviderApplicationUrlResolverFactory
{
    private readonly IReadOnlyDictionary<string, IProviderApplicationUrlResolver> _providers;

    public ProviderApplicationUrlResolverFactory(IEnumerable<IProviderApplicationUrlResolver> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderApplicationUrlResolver? GetResolver(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class ProviderApplicationConfigSyncFactory : IProviderApplicationConfigSyncFactory
{
    private readonly IReadOnlyDictionary<string, IProviderApplicationConfigSync> _providers;

    public ProviderApplicationConfigSyncFactory(IEnumerable<IProviderApplicationConfigSync> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderApplicationConfigSync? GetConfigSync(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
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

public sealed class ProviderLifecycleOperationsFactory : IProviderLifecycleOperationsFactory
{
    private readonly IReadOnlyDictionary<string, IProviderLifecycleOperations> _providers;

    public ProviderLifecycleOperationsFactory(IEnumerable<IProviderLifecycleOperations> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderLifecycleOperations? GetLifecycleOperations(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class ProviderRuntimeLogsFactory : IProviderRuntimeLogsFactory
{
    private readonly IReadOnlyDictionary<string, IProviderRuntimeLogs> _providers;

    public ProviderRuntimeLogsFactory(IEnumerable<IProviderRuntimeLogs> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IProviderRuntimeLogs? GetRuntimeLogs(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class ServerAddressProviderFactory : IServerAddressProviderFactory
{
    private readonly IReadOnlyDictionary<string, IServerAddressProvider> _providers;

    public ServerAddressProviderFactory(IEnumerable<IServerAddressProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IServerAddressProvider? GetServerAddressProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;
}

public sealed class DnsZoneProviderFactory : IDnsZoneProviderFactory
{
    private readonly IReadOnlyDictionary<string, IDnsZoneProvider> _providers;

    public DnsZoneProviderFactory(IEnumerable<IDnsZoneProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IDnsZoneProvider? GetZoneProvider(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;

    public IReadOnlyList<IDnsZoneProvider> All => _providers.Values.ToList();
}

public sealed class ApplicationDomainAssignmentFactory : IApplicationDomainAssignmentFactory
{
    private readonly IReadOnlyDictionary<string, IApplicationDomainAssignment> _providers;

    public ApplicationDomainAssignmentFactory(IEnumerable<IApplicationDomainAssignment> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IApplicationDomainAssignment? GetDomainAssignment(string providerName) =>
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

public sealed class ObjectStorageProviderFactory : IObjectStorageProviderFactory
{
    private readonly IReadOnlyDictionary<string, IObjectStorageProvider> _providers;

    public ObjectStorageProviderFactory(IEnumerable<IObjectStorageProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IObjectStorageProvider? GetObjectStorage(string providerName) =>
        _providers.TryGetValue(providerName, out var provider) ? provider : null;

    public IReadOnlyList<ObjectStorageProviderInfo> GetAvailableProviders() =>
        _providers.Values
            .Select(p => new ObjectStorageProviderInfo(p.ProviderName, p.DisplayName))
            .ToList();
}

/// <summary>Registers every provider implementation and their capability factories with DI. Adding support for a new capability on a provider means registering it as that interface here too.</summary>
public static class ProviderDependencyInjection
{
    /// <summary>Registers Vercel, Railway, and Coolify (and their optional capability interfaces), plus each capability's resolving factory.</summary>
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
        services.AddSingleton<IProviderDataServiceInspection>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddHttpClient<Coolify.CoolifyProvider>();
        services.AddSingleton<Coolify.CoolifyProvider>();
        services.AddSingleton<IDeploymentProvider>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderManagement>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderDatabaseProvisioning>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderApplicationUrlResolver>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderApplicationConfigSync>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderServiceOperations>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderLifecycleOperations>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IProviderRuntimeLogs>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IServerAddressProvider>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        services.AddSingleton<IApplicationDomainAssignment>(sp => sp.GetRequiredService<Coolify.CoolifyProvider>());
        // Object storage is a separate capability: registered only as IObjectStorageProvider,
        // never as IDeploymentProvider, so it stays out of deploy-target pickers.
        // No AddHttpClient — the AWS SDK manages its own transport.
        services.AddSingleton(_ => new HetznerStorage.HetznerStorageProvider());
        services.AddSingleton<IObjectStorageProvider>(sp => sp.GetRequiredService<HetznerStorage.HetznerStorageProvider>());
        services.AddSingleton<IObjectStorageProviderFactory, ObjectStorageProviderFactory>();
        services.AddSingleton<IProviderApplicationUrlResolverFactory, ProviderApplicationUrlResolverFactory>();
        services.AddSingleton<IProviderApplicationConfigSyncFactory, ProviderApplicationConfigSyncFactory>();
        services.AddSingleton<IProviderDatabaseProvisioningFactory, ProviderDatabaseProvisioningFactory>();
        services.AddSingleton<IProviderServiceOperationsFactory, ProviderServiceOperationsFactory>();
        services.AddSingleton<IProviderRuntimeLogsFactory, ProviderRuntimeLogsFactory>();
        services.AddSingleton<IProviderLifecycleOperationsFactory, ProviderLifecycleOperationsFactory>();
        services.AddSingleton<IProviderDataServiceInspectionFactory, ProviderDataServiceInspectionFactory>();
        services.AddSingleton<IServerAddressProviderFactory, ServerAddressProviderFactory>();
        services.AddSingleton<IApplicationDomainAssignmentFactory, ApplicationDomainAssignmentFactory>();
        // DNS is its own capability, registered only as IDnsZoneProvider so a DNS account never
        // appears in a deploy-target picker — the same separation object storage already has.
        services.AddHttpClient<Cloudflare.CloudflareDnsProvider>();
        services.AddSingleton<Cloudflare.CloudflareDnsProvider>();
        services.AddSingleton<IDnsZoneProvider>(sp => sp.GetRequiredService<Cloudflare.CloudflareDnsProvider>());
        services.AddHttpClient<Porkbun.PorkbunDnsProvider>();
        services.AddSingleton<Porkbun.PorkbunDnsProvider>();
        services.AddSingleton<IDnsZoneProvider>(sp => sp.GetRequiredService<Porkbun.PorkbunDnsProvider>());
        // The approval flow is optional per provider: Cloudflare has none, so it is registered
        // only for Porkbun and the paste path remains the fallback everywhere.
        services.AddHttpClient<Porkbun.PorkbunAuthorizationFlow>();
        services.AddSingleton<IDnsAuthorizationFlow>(sp =>
            sp.GetRequiredService<Porkbun.PorkbunAuthorizationFlow>());
        services.AddSingleton<IDnsZoneProviderFactory, DnsZoneProviderFactory>();
        services.AddSingleton<IProviderFactory, ProviderFactory>();
        services.AddSingleton<IProviderManagementFactory, ProviderManagementFactory>();
        return services;
    }
}
