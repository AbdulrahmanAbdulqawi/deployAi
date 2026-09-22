using System.Text.RegularExpressions;

namespace DeployAI.Infrastructure.GitHub;

/// <summary>The app project inside a directory, and what its project file says.</summary>
public sealed record LocatedDotnetProject(
    /// <summary>Path relative to the directory searched ("TicketHub.Server/TicketHub.Server.csproj").</summary>
    string Path,
    string FileName,
    string Content)
{
    /// <summary>The directory the project lives in, relative to the one searched; empty at its root.</summary>
    public string Directory
    {
        get
        {
            var lastSlash = Path.LastIndexOf('/');
            return lastSlash < 0 ? string.Empty : Path[..lastSlash];
        }
    }
}

public interface IDotnetProjectLocator
{
    /// <summary>
    /// The runnable .NET project a directory holds, directly or through a solution it contains.
    /// </summary>
    Task<LocatedDotnetProject?> LocateAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Finds the project that <em>is</em> the app, for a directory that may only contain a solution.
/// </summary>
/// <remarks>
/// <para>
/// A multi-project .NET solution puts the API in one directory and the projects it references in
/// siblings, so everything about the deployment is rooted at the directory holding them all — and
/// that directory has a <c>.sln</c> and no <c>.csproj</c>. Anything that looks for the app's files
/// there finds nothing: no Dockerfile was generated for TicketHub's API because of this, and its
/// database requirement read as "none" because <c>appsettings.json</c> was looked for at the root
/// rather than beside the project.
/// </para>
/// <para>
/// Shared because both of those are the same question asked by different callers. Two copies would
/// answer differently the day one is fixed, and the difference would show up as a deployment that
/// is missing one file rather than as an obvious disagreement.
/// </para>
/// </remarks>
public sealed class DotnetProjectLocator(IGitHubService gitHub) : IDotnetProjectLocator
{
    private readonly IGitHubService _gitHub = gitHub;

    /// <summary>Enough to cover a real solution without paying a request per project on a huge one.</summary>
    private const int MaxSolutionProjectsRead = 12;

    public async Task<LocatedDotnetProject?> LocateAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        CancellationToken cancellationToken)
    {
        var contents = await _gitHub.ListAllContentsAsync(token, owner, repo, directory, branch, cancellationToken);
        var files = contents
            .Where(item => string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .ToList();

        return await LocateAsync(token, owner, repo, branch, directory, files, cancellationToken);
    }

    /// <summary>For a caller that has already listed the directory and should not list it twice.</summary>
    public async Task<LocatedDotnetProject?> LocateAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string directory,
        IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        // A project sitting in the directory itself is the simple case and answers first. Handling
        // only the solution case made this method's name a lie — a caller asking "which project is
        // the app here" got nothing for the most ordinary layout there is.
        var direct = await LocateDirectProjectAsync(token, owner, repo, branch, directory, files, cancellationToken);
        if (direct is not null)
        {
            return direct;
        }

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
            // A name that reads like the app is tried first; the SDK check is what decides.
            .OrderByDescending(LooksLikeAnApp)
            .Take(MaxSolutionProjectsRead)
            .ToList();

        foreach (var candidate in candidates)
        {
            var content = await _gitHub.GetFileContentAsync(
                token, owner, repo, Join(directory, candidate), branch, cancellationToken);

            // Nothing is claimed when no project carries a web or worker SDK: a solution of
            // libraries has no entry point, and picking one produces an image that starts nothing.
            if (content is not null && DeclaresRunnableSdk(content))
            {
                return new LocatedDotnetProject(candidate, Path.GetFileName(candidate), content);
            }
        }

        return null;
    }

    /// <summary>
    /// The runnable project in this directory. A directory holding both a library and a web project
    /// resolves by SDK rather than by listing order — building the library produces an image with
    /// no entry point.
    /// </summary>
    private async Task<LocatedDotnetProject?> LocateDirectProjectAsync(
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

        foreach (var candidate in candidates)
        {
            var content = await _gitHub.GetFileContentAsync(
                token, owner, repo, Join(directory, candidate), branch, cancellationToken);

            if (content is not null && DeclaresRunnableSdk(content))
            {
                return new LocatedDotnetProject(candidate, candidate, content);
            }
        }

        return null;
    }

    private static bool LooksLikeAnApp(string projectPath) =>
        projectPath.Contains("server", StringComparison.OrdinalIgnoreCase) ||
        projectPath.Contains("api", StringComparison.OrdinalIgnoreCase) ||
        projectPath.Contains("web", StringComparison.OrdinalIgnoreCase);

    private static bool DeclaresRunnableSdk(string csproj) =>
        csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
        csproj.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase);

    private static string Join(string directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : $"{directory.TrimEnd('/')}/{name}";
}
