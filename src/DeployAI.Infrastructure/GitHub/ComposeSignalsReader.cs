using DeployAI.Core.Deployments.Adapters;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// The per-directory evidence for a set of compose build contexts, plus the contexts that could
/// not be read at all. The second list is not an afterthought: empty signals and unreadable
/// signals lead an adapter to the same silence for opposite reasons.
/// </summary>
public sealed record ComposeSignalsScan(
    IReadOnlyDictionary<string, RepositorySignals> SignalsByDirectory,
    IReadOnlyList<string> UnreadableDirectories);

public interface IComposeSignalsReader
{
    Task<ComposeSignalsScan> ReadAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        IReadOnlyCollection<string> directories,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fills the signals dictionary <see cref="ComposeGraphBuilder"/> consumes.
/// </summary>
/// <remarks>
/// <para>
/// Each directory is listed once and only the files that exist are fetched. The alternative —
/// asking for every file an adapter might want — is a request per file per service on every
/// scan, and a compose file can name half a dozen contexts.
/// </para>
/// <para>
/// A context that lists nothing is reported rather than given empty signals. The two are
/// indistinguishable to an adapter (both produce no claim), but they mean opposite things: one
/// says the directory holds nothing recognisable, the other says nobody looked. GitHub itself
/// cannot tell them apart here — <c>ListContentsAsync</c> returns an empty list for a missing
/// path and for a genuinely empty one — so a context named by a compose file that lists nothing
/// is treated as unread, which is the safer of the two readings.
/// </para>
/// </remarks>
public sealed class ComposeSignalsReader(IGitHubService gitHub) : IComposeSignalsReader
{
    private readonly IGitHubService _gitHub = gitHub;

    public async Task<ComposeSignalsScan> ReadAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        IReadOnlyCollection<string> directories,
        CancellationToken cancellationToken)
    {
        var signals = new Dictionary<string, RepositorySignals>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();

        // Distinct: two services commonly build from one directory (an API and its worker), and
        // listing it twice is a request bought for nothing.
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // ListAllContentsAsync, not ListContentsAsync: the latter filters its result to
            // `type == "dir"` and can never return a file, so filtering it for files below found
            // nothing, every context was reported unreadable, and three deployed compose apps were
            // told their build directories held no Dockerfile. The unit tests did not catch it
            // because the fake served files from the dirs-only method.
            var contents = await _gitHub.ListAllContentsAsync(token, owner, repo, directory, branch, cancellationToken);
            var files = contents
                .Where(item => string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Name)
                .ToList();

            if (files.Count == 0)
            {
                unreadable.Add(directory);
                continue;
            }

            signals[directory] = await ReadSignalsAsync(token, owner, repo, branch, directory, files, cancellationToken);
        }

        return new ComposeSignalsScan(signals, unreadable);
    }

    private async Task<RepositorySignals> ReadSignalsAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        Task<string?> Read(string name) =>
            Has(files, name)
                ? _gitHub.GetFileContentAsync(token, owner, repo, Join(directory, name), branch, cancellationToken)
                : Task.FromResult<string?>(null);

        var csprojName = await ResolveCsprojAsync(token, owner, repo, branch, directory, files, cancellationToken);

        return new RepositorySignals(
            directory,
            PackageJson: await Read("package.json"),
            AngularJson: await Read("angular.json"),
            CsprojContent: csprojName is null
                ? null
                : await _gitHub.GetFileContentAsync(token, owner, repo, Join(directory, csprojName), branch, cancellationToken),
            CsprojFileName: csprojName,
            HasDockerfile: Has(files, "Dockerfile"),
            DockerfileContent: await Read("Dockerfile"),
            RequirementsTxt: await Read("requirements.txt"),
            PyprojectToml: await Read("pyproject.toml"),
            GoMod: await Read("go.mod"),
            CargoToml: await Read("Cargo.toml"),
            AppsettingsJson: await Read("appsettings.json"),
            ViteConfig: await ReadFirstAsync(
                token, owner, repo, branch, directory, files, cancellationToken,
                "vite.config.ts", "vite.config.js", "vite.config.mjs"));
    }

    /// <summary>
    /// The first of several alternative spellings that the directory actually holds. A config file
    /// with three accepted extensions is one file, not three, so this fetches at most one.
    /// </summary>
    private async Task<string?> ReadFirstAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken,
        params string[] names)
    {
        foreach (var name in names.Where(name => Has(files, name)))
        {
            return await _gitHub.GetFileContentAsync(
                token, owner, repo, Join(directory, name), branch, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Picks the csproj that marks a runnable application. A directory holding both a library and
    /// a web project would otherwise resolve by listing order, and building the library produces
    /// an image with no entry point — the failure <see cref="RepositoryLayoutResolver"/> exists to
    /// prevent, repeated here because this reader resolves a context directly rather than through
    /// a layout.
    /// </summary>
    private async Task<string?> ResolveCsprojAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var candidates = files
            .Where(name => name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        foreach (var candidate in candidates)
        {
            var content = await _gitHub.GetFileContentAsync(
                token, owner, repo, Join(directory, candidate), branch, cancellationToken);

            if (content is not null &&
                (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static bool Has(IReadOnlyList<string> files, string name) =>
        files.Any(file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase));

    private static string Join(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory.TrimEnd('/')}/{name}";
}
