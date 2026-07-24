using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Core.Deployments.Planning;

/// <summary>
/// One decided operation. Steps are decisions, not renderings: the executor carries them out
/// against the provider, generators never see them.
/// </summary>
public abstract record DeploymentStep;

/// <summary>Create the provider application for the deployable services, or reuse the existing one.</summary>
public sealed record EnsureApplicationStep(bool Reuse) : DeploymentStep;

/// <summary>Provision (or reuse) a bucket for an ObjectStorage node.</summary>
public sealed record ProvisionBucketStep(string ServiceId, bool Reuse) : DeploymentStep;

/// <summary>Provision (or reuse) a database for a Database node the target hosts as a managed resource.</summary>
public sealed record ProvisionDatabaseStep(string ServiceId, bool Reuse) : DeploymentStep;

/// <summary>Write env vars onto the application before they are needed. Keys only — values come from the caller.</summary>
public sealed record SetEnvVarsStep(string ServiceId, IReadOnlyList<string> Keys) : DeploymentStep;

/// <summary>Trigger a deployment. Ordinal distinguishes the first build from later ones.</summary>
public sealed record DeployStep(int Ordinal) : DeploymentStep;

/// <summary>Attach an ingress rule's domain to its service.</summary>
public sealed record AssignDomainStep(IngressRule Ingress) : DeploymentStep;

public sealed record DeploymentExecutionPlan(IReadOnlyList<DeploymentStep> Steps)
{
    public static DeploymentExecutionPlan Empty { get; } = new([]);
}

/// <summary>
/// What already exists on the provider — the planner flips create-vs-reuse from this rather
/// than blindly creating, so re-running a deployment is idempotent instead of duplicative.
/// </summary>
public sealed record ProviderStateSnapshot(
    bool ApplicationExists = false,
    bool DomainAssigned = false,
    IReadOnlyCollection<string>? ExistingBuckets = null)
{
    public IReadOnlyCollection<string> Buckets => ExistingBuckets ?? [];

    public static ProviderStateSnapshot Fresh { get; } = new();
}

public interface IDeploymentPlanner
{
    /// <summary>The deploy target whose constraints this planner encodes ("coolify-compose").</summary>
    string Target { get; }

    DeploymentExecutionPlan Plan(DeploymentGraph graph, ProviderStateSnapshot state);
}
