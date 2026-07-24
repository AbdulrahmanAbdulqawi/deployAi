using DeployAI.Core.Deployments.Graph;

namespace DeployAI.Core.Deployments.Adapters;

/// <summary>
/// The framework-specific plugin. Everything the deployment engine must never know — build
/// commands, Dockerfile layout, health endpoint conventions, env key style — lives behind this
/// interface. Adding a framework means writing an adapter; the shapes, planner, and generators
/// stay untouched.
/// </summary>
public interface IFrameworkAdapter
{
    /// <summary>Stable id recorded on the ServiceNode ("angular", "dotnet", "node-express").</summary>
    string Id { get; }

    /// <summary>Health path convention (/api/health, /health, /actuator/health, /up); null if none.</summary>
    string? HealthPath { get; }

    /// <summary>How this framework reads config, so wiring emits keys the app actually binds.</summary>
    EnvKeyConvention EnvConvention { get; }

    /// <summary>
    /// Does this directory look like mine? Null means no. Confidence lets the graph builder
    /// rank competing claims (a package.json with @angular/core is Angular's, not generic Node's).
    /// </summary>
    AdapterDetection? Detect(RepositorySignals signals);

    /// <summary>
    /// Builds the graph node for a directory this adapter claimed: capabilities, build source
    /// with a generated (or repo-provided) Dockerfile, ports, health, volumes.
    /// </summary>
    ServiceNode CreateServiceNode(string serviceId, RepositorySignals signals);
}

/// <summary>
/// The files that describe one candidate service directory. Assembled once by the graph
/// builder from what the discovery services already fetch — adapters never do I/O.
/// </summary>
public sealed record RepositorySignals(
    /// <summary>Repo-relative directory this bundle describes ("" for root, "client", "server/Api").</summary>
    string Directory,
    string? PackageJson = null,
    string? AngularJson = null,
    string? CsprojContent = null,
    string? CsprojFileName = null,
    bool HasDockerfile = false,
    string? DockerfileContent = null,
    string? RequirementsTxt = null,
    string? PyprojectToml = null,
    string? GoMod = null,
    string? CargoToml = null,
    string? AppsettingsJson = null);

public sealed record AdapterDetection(
    /// <summary>0..1. Specific signals (angular.json, a csproj) score high; generic ones (bare package.json) low.</summary>
    double Confidence,
    ServiceCapability Capabilities);

/// <summary>
/// The env key style the framework's configuration binder expects. Wiring that emits the
/// wrong style fails silently: the app reads nothing and falls back to defaults.
/// </summary>
public enum EnvKeyConvention
{
    /// <summary>ASP.NET Core configuration binding: Section__Key (Storage__Endpoint).</summary>
    DotnetSectionKey,

    /// <summary>Flat upper-snake (S3_ENDPOINT, DATABASE_URL) — Node/Python/Go convention.</summary>
    FlatUpperSnake
}

public interface IFrameworkAdapterFactory
{
    IReadOnlyList<IFrameworkAdapter> All { get; }

    IFrameworkAdapter? GetById(string id);
}
