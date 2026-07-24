using DeployAI.Core.Deployments.Adapters;

namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// Same pattern as the provider factories: DI collects every registered adapter, this resolves
/// them. Adding a framework is one class plus one registration — nothing else changes.
/// </summary>
public sealed class FrameworkAdapterFactory : IFrameworkAdapterFactory
{
    private readonly IReadOnlyList<IFrameworkAdapter> _adapters;

    public FrameworkAdapterFactory(IEnumerable<IFrameworkAdapter> adapters)
    {
        _adapters = adapters.ToArray();
    }

    public IReadOnlyList<IFrameworkAdapter> All => _adapters;

    public IFrameworkAdapter? GetById(string id) =>
        _adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
