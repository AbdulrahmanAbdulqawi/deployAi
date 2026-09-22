using System.Text.RegularExpressions;
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
        var csprojContent = csprojName is null
            ? null
            : await _gitHub.GetFileContentAsync(token, owner, repo, Join(directory, csprojName), branch, cancellationToken);

        // Only when the context holds no project file of its own. A directory with both a csproj
        // and a solution is the ordinary single-project case, and giving it a path would switch it
        // to the root-context image every app deployed from it does not build with.
        var throughSolution = csprojName is null
            ? await ResolveProjectThroughSolutionAsync(token, owner, repo, branch, directory, files, cancellationToken)
            : null;

        return new RepositorySignals(
            directory,
            PackageJson: await Read("package.json"),
            AngularJson: await Read("angular.json"),
            CsprojContent: csprojContent ?? throughSolution?.Content,
            CsprojFileName: csprojName ?? throughSolution?.FileName,
            HasDockerfile: Has(files, "Dockerfile"),
            DockerfileContent: await Read("Dockerfile"),
            RequirementsTxt: await Read("requirements.txt"),
            PyprojectToml: await Read("pyproject.toml"),
            GoMod: await Read("go.mod"),
            CargoToml: await Read("Cargo.toml"),
            AppsettingsJson: await Read("appsettings.json"),
            ViteConfig: await ReadFirstAsync(
                token, owner, repo, branch, directory, files, cancellationToken,
                "vite.config.ts", "vite.config.js", "vite.config.mjs"),
            ProjectFilePath: throughSolution?.Path);
    }

    private sealed record SolutionProject(string Path, string FileName, string Content);

    /// <summary>
    /// The runnable app inside a solution, for a build context that holds no project file itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape a multi-project .NET solution forces: the API references its siblings, so
    /// the build has to run from the directory that contains them all, and that directory has only
    /// a <c>.sln</c>. Without this nothing claims the context, no Dockerfile is generated, and the
    /// setup commits every file but the one the API needs — observed on TicketHub, three of four.
    /// </para>
    /// <para>
    /// The solution names the projects; reading them is what says which is the app. Nothing is
    /// claimed when none carries a web or worker SDK: a solution of libraries has no entry point,
    /// and picking one anyway produces an image that starts nothing — the same failure the
    /// csproj ranking below exists to prevent one level down. Candidates are read in a likely
    /// order and capped, because a large solution would otherwise cost a request per project on
    /// every scan.
    /// </para>
    /// </remarks>
    private async Task<SolutionProject?> ResolveProjectThroughSolutionAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        var solutionName = files.FirstOrDefault(name => name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (solutionName is null)
        {
            return null;
        }

        var solution = await _gitHub.GetFileContentAsync(
            token, owner, repo, Join(directory, solutionName), branch, cancellationToken);
        if (string.IsNullOrWhiteSpace(solution))
        {
            return null;
        }

        var candidates = Regex
            .Matches(solution, @"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]+"",\s*""([^""]+\.csproj)""",
                RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value.Replace('\\', '/').Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // A name that reads like the app is tried first; the SDK check below is what decides.
            .OrderByDescending(LooksLikeAnApp)
            .Take(MaxSolutionProjectsRead)
            .ToList();

        foreach (var candidate in candidates)
        {
            var content = await _gitHub.GetFileContentAsync(
                token, owner, repo, Join(directory, candidate), branch, cancellationToken);

            if (content is not null && DeclaresRunnableSdk(content))
            {
                return new SolutionProject(candidate, Path.GetFileName(candidate), content);
            }
        }

        return null;
    }

    /// <summary>Enough to cover a real solution without paying a request per project on a huge one.</summary>
    private const int MaxSolutionProjectsRead = 12;

    private static bool LooksLikeAnApp(string projectPath) =>
        projectPath.Contains("server", StringComparison.OrdinalIgnoreCase) ||
        projectPath.Contains("api", StringComparison.OrdinalIgnoreCase) ||
        projectPath.Contains("web", StringComparison.OrdinalIgnoreCase);

    private static bool DeclaresRunnableSdk(string csproj) =>
        csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
        csproj.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);

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
