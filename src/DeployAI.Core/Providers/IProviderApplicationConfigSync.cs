namespace DeployAI.Core.Providers;

/// <summary>Build pack/directories/commands to push to a provider application's live config.</summary>
/// <param name="ExposedPort">The port the running container actually listens on. Supply it whenever
/// it is known: without it a sync re-guesses from the framework name, and a guess that disagrees
/// with the image points the proxy at a closed port — the container runs, the deploy reports
/// success, and every request gets a 502 from the proxy rather than anything the app said.</param>
public sealed record UpdateProviderApplicationConfigRequest(
    string? Framework,
    string? RootDirectory = null,
    string? OutputDirectory = null,
    string? BuildCommand = null,
    string? InstallCommand = null,
    string? StartCommand = null,
    string? DockerfilePath = null,
    string? CoolifyBuildPack = null,
    string? ExposedPort = null,
    /// <summary>
    /// Set when the application is one Docker Compose resource. Without it this request describes a
    /// compose deployment as a plain website — a framework and a build command — and the build pack
    /// is resolved from those, which answers Nixpacks. That overwrites the compose selection made
    /// when the application was created, so the deploy builds the client directory on its own and
    /// the API and database in the compose file are never built at all.
    /// </summary>
    string? ComposeFileLocation = null);

/// <summary>
/// Optional capability for providers (currently only Coolify) whose application settings must be
/// pushed to the live provider explicitly - unlike Railway/Vercel, which pick up config changes
/// from environment variables or repo files alone, Coolify's build pack/port/commands only take
/// effect through its own application PATCH endpoint.
/// </summary>
public interface IProviderApplicationConfigSync
{
    string ProviderName { get; }

    /// <summary>Pushes current build/directory/command config to the live provider application.</summary>
    /// <param name="credentials">The connection to authenticate with.</param>
    /// <param name="providerProjectId">The provider-side application id.</param>
    /// <param name="request">The config to push.</param>
    Task UpdateApplicationConfigAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpdateProviderApplicationConfigRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Resolves the optional <see cref="IProviderApplicationConfigSync"/> capability for a provider, if it supports one.</summary>
public interface IProviderApplicationConfigSyncFactory
{
    IProviderApplicationConfigSync? GetConfigSync(string providerName);
}
