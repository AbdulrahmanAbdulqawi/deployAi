using DeployAI.Core.Providers;

namespace DeployAI.Core.Deployments;

/// <summary>
/// Decides whether a full-stack repo should be deployed as one Docker Compose resource
/// serving everything from a single origin, instead of two separately-addressed apps.
///
/// This lives in Core rather than next to the wiring code because both the classifier
/// (Infrastructure, when picking a <see cref="DeploymentPlanKind"/>) and the wiring/readiness
/// code (Api) have to agree on the answer — if they disagreed, we would generate compose
/// templates and then evaluate them against split-origin readiness rules.
/// </summary>
public static class SingleOriginComposeShape
{
    /// <summary>
    /// Deliberately narrow: only the stack we actually generate compose templates for.
    /// A frontend with its own server runtime (Next, Nuxt, SvelteKit, Astro) is not a static
    /// bundle nginx can front, so it stays on the split-origin path until it has templates.
    /// </summary>
    public static bool SupportsFrameworks(string? websiteFramework, string? serverFramework) =>
        IsStaticBundleFrontend(websiteFramework) && IsDotnetServer(serverFramework);

    /// <summary>
    /// Both halves must land on the same Coolify server for one compose file to host them.
    /// </summary>
    public static bool SupportsProviders(string? websiteProvider, string? serverProvider) =>
        IsCoolify(websiteProvider) && IsCoolify(serverProvider);

    public static bool Supports(
        string? websiteFramework,
        string? serverFramework,
        string? websiteProvider,
        string? serverProvider) =>
        SupportsProviders(websiteProvider, serverProvider) &&
        SupportsFrameworks(websiteFramework, serverFramework);

    private static bool IsStaticBundleFrontend(string? framework) =>
        framework?.ToLowerInvariant() is "angular";

    private static bool IsDotnetServer(string? framework) =>
        framework?.ToLowerInvariant() is "dotnet" or "aspnet" or "aspnetcore" or "docker";

    private static bool IsCoolify(string? providerName) =>
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);
}
