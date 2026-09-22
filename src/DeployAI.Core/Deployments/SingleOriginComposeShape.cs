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
    /// Any front end that compiles to static files, in front of a .NET server.
    /// </summary>
    /// <remarks>
    /// This was <c>"angular"</c> and nothing else, back when the shape meant "the stack we have
    /// compose templates for". It does not mean that any more: the files come from the deployment
    /// graph, which asks an adapter what a directory is and contains no framework name. What still
    /// has to hold is that the front end is a bundle nginx can serve — a framework with its own
    /// server runtime (Next, Nuxt, SvelteKit, Remix, Astro) is not, and serving its build output
    /// through nginx drops half the app.
    ///
    /// Widening this alone would have shipped broken apps: a bundle that bakes its API base in at
    /// build time needs that value present when the image is built, which the compose shape never
    /// provided. That is wired first, in <c>FrontendEnvironmentWiringService</c>.
    /// </remarks>
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
        framework?.ToLowerInvariant() is "angular" or "vite" or "react" or "vue" or "svelte" or "preact" or "solid";

    private static bool IsDotnetServer(string? framework) =>
        framework?.ToLowerInvariant() is "dotnet" or "aspnet" or "aspnetcore" or "docker";

    private static bool IsCoolify(string? providerName) =>
        string.Equals(providerName, ProviderNameValues.Coolify, StringComparison.OrdinalIgnoreCase);
}
