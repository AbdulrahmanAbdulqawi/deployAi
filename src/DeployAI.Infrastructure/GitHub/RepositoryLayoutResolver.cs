namespace DeployAI.Infrastructure.GitHub;

/// <summary>
/// Where an app actually lives inside a repository.
/// </summary>
/// <param name="BuildRoot">The directory the caller was told to look in — the Docker build context.</param>
/// <param name="ProjectDirectory">Where the app's own files are. Equals <paramref name="BuildRoot"/>
/// for a single-project repo; one level deeper for a modular solution.</param>
/// <param name="EntryProjectPath">The manifest that identified the app — a web csproj, a package.json
/// with a start script, and so on. Null when nothing identified it.</param>
/// <param name="SearchPath">Directories to look in, nearest first. Reading through this is what makes
/// a monorepo visible without a repository crawl.</param>
/// <param name="DirectoriesRead">Every directory actually listed. The difference between "this app
/// declares no configuration" and "I could not see the app" lives here.</param>
public sealed record RepositoryLayout(
    string BuildRoot,
    string ProjectDirectory,
    string? EntryProjectPath,
    IReadOnlyList<string> SearchPath,
    IReadOnlyList<string> DirectoriesRead)
{
    /// <summary>
    /// True when nothing could be listed at all. A caller must never treat this the same as an
    /// empty-but-trustworthy result: it means the scan was blind, not that the repo is bare.
    /// </summary>
    public bool IsInconclusive => DirectoriesRead.Count == 0;
}

/// <summary>A file found through a layout, with the path it was actually found at.</summary>
public sealed record RepositoryFile(string Path, string Content);

public interface IRepositoryLayoutResolver
{
    Task<RepositoryLayout> ResolveAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string? configuredDirectory,
        CancellationToken cancellationToken);
}

public interface IRepositoryReader
{
    /// <summary>The nearest file with this name along the layout's search path, or null.</summary>
    Task<RepositoryFile?> FindAsync(
        string token, string owner, string repo, string? branch,
        RepositoryLayout layout, string fileName, CancellationToken cancellationToken);

    /// <summary>Every file along the search path whose name ends with this suffix.</summary>
    Task<IReadOnlyList<RepositoryFile>> FindAllBySuffixAsync(
        string token, string owner, string repo, string? branch,
        RepositoryLayout layout, string suffix, CancellationToken cancellationToken);
}

/// <summary>
/// The single answer to "where does this app live in the repository", replacing a guess made
/// independently in every caller that needed it.
/// </summary>
/// <remarks>
/// Seven scanners each assumed a layout, and a wrong assumption returned nothing rather than an
/// error — indistinguishable from "this repo has no such file". In one day on one monorepo that
/// silently produced: an env scan finding zero variables, a server Dockerfile never regenerated,
/// object storage never provisioned, and no check on the configuration the deployed code needed. Two
/// were patched by adding a bespoke "look one level down" to that one caller, which is why it kept
/// recurring. See <c>docs/12-repository-scanning.md</c>.
/// <para>
/// Resolution is by evidence, not by convention: the directory holding the entry project wins,
/// where "entry" means the manifest that marks a runnable application rather than the first manifest
/// encountered. Building <c>YemenHub.Modules</c> instead of <c>YemenHub.Api</c> produces an image
/// with no entry point, which is why the distinction matters.
/// </para>
/// </remarks>
public sealed class RepositoryLayoutResolver : IRepositoryLayoutResolver, IRepositoryReader
{
    private readonly IGitHubService _gitHub;

    public RepositoryLayoutResolver(IGitHubService gitHub)
    {
        _gitHub = gitHub;
    }

    public async Task<RepositoryLayout> ResolveAsync(
        string token,
        string owner,
        string repo,
        string? branch,
        string? configuredDirectory,
        CancellationToken cancellationToken)
    {
        var buildRoot = Normalize(configuredDirectory);
        var directoriesRead = new List<string>();

        var contents = await ListAsync(token, owner, repo, buildRoot, branch, directoriesRead, cancellationToken);

        // Directly in the configured directory: a single-project repo, nothing to descend into.
        if (await FindEntryProjectAsync(token, owner, repo, branch, contents, cancellationToken) is { } here)
        {
            return new RepositoryLayout(
                buildRoot, buildRoot, here.Path, SearchPathFor(buildRoot, buildRoot), directoriesRead);
        }

        // One level down. Deeper is a repository crawl, and the cost is paid on every deploy.
        foreach (var directory in contents.Where(IsDirectory))
        {
            var inner = await ListAsync(
                token, owner, repo, directory.Path, branch, directoriesRead, cancellationToken);

            if (await FindEntryProjectAsync(token, owner, repo, branch, inner, cancellationToken) is { } nested)
            {
                var projectDirectory = Normalize(DirectoryOf(nested.Path));
                return new RepositoryLayout(
                    buildRoot,
                    projectDirectory,
                    nested.Path,
                    SearchPathFor(projectDirectory, buildRoot),
                    directoriesRead);
            }
        }

        // Nothing identified an app. The search path still covers what was looked at, so a caller
        // can read config files from a repo this resolver could not classify — and DirectoriesRead
        // says whether it was blind or merely unsuccessful.
        return new RepositoryLayout(
            buildRoot,
            buildRoot,
            null,
            SearchPathFor(buildRoot, buildRoot, contents.Where(IsDirectory).Select(d => d.Path)),
            directoriesRead);
    }

    public async Task<RepositoryFile?> FindAsync(
        string token, string owner, string repo, string? branch,
        RepositoryLayout layout, string fileName, CancellationToken cancellationToken)
    {
        foreach (var directory in layout.SearchPath)
        {
            var path = Join(directory, fileName);
            var content = await _gitHub.GetFileContentAsync(token, owner, repo, path, branch, cancellationToken);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return new RepositoryFile(path, content);
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<RepositoryFile>> FindAllBySuffixAsync(
        string token, string owner, string repo, string? branch,
        RepositoryLayout layout, string suffix, CancellationToken cancellationToken)
    {
        var found = new List<RepositoryFile>();

        foreach (var directory in layout.SearchPath)
        {
            IReadOnlyList<GitHubContentItem> contents;
            try
            {
                // Null, not just empty: a listing for a path that does not exist comes back without
                // a collection, and dereferencing it turns a missing directory into a crash.
                contents = await _gitHub.ListAllContentsAsync(token, owner, repo, directory, branch, cancellationToken)
                    ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            foreach (var item in contents.Where(i =>
                         !IsDirectory(i) && i.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            {
                var content = await _gitHub.GetFileContentAsync(token, owner, repo, item.Path, branch, cancellationToken);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    found.Add(new RepositoryFile(item.Path, content));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The manifest that marks a runnable application, as opposed to a library sitting beside it.
    /// </summary>
    /// <remarks>
    /// A .csproj is judged by its contents, never by being the first one seen. One repository's
    /// service directory holds four — Api, Modules, Persistence, SharedKernel — and only the first
    /// is an application; publishing any of the others yields an image with no entry point. The
    /// SDK attribute is the evidence, so the file has to be read.
    /// </remarks>
    private async Task<GitHubContentItem?> FindEntryProjectAsync(
        string token, string owner, string repo, string? branch,
        IReadOnlyList<GitHubContentItem> contents, CancellationToken cancellationToken)
    {
        var files = contents.Where(i => !IsDirectory(i)).ToList();

        // Unambiguous by name: these exist only in an application's own directory.
        var byName = files.FirstOrDefault(f => f.Name.Equals("manage.py", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.Name.Equals("go.mod", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        foreach (var csproj in files.Where(f => f.Name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var content = await _gitHub.GetFileContentAsync(
                token, owner, repo, csproj.Path, branch, cancellationToken);

            if (content is not null && HostedAppSdks.Any(sdk =>
                    content.Contains(sdk, StringComparison.OrdinalIgnoreCase)))
            {
                return csproj;
            }
        }

        // A directory of libraries and nothing else. Returning null is the honest answer; guessing
        // one of them is how a build ends up with no entry point.
        return null;
    }

    /// <summary>SDKs that produce a hosted application rather than a library.</summary>
    private static readonly string[] HostedAppSdks =
        ["Microsoft.NET.Sdk.Web", "Microsoft.NET.Sdk.Worker"];

    /// <summary>
    /// Nearest first: the app's own directory, then the build root, then the repository root. A file
    /// present in more than one place resolves to the copy closest to the app, which is the one it
    /// actually reads.
    /// </summary>
    private static IReadOnlyList<string> SearchPathFor(
        string projectDirectory,
        string buildRoot,
        IEnumerable<string>? extra = null)
    {
        var path = new List<string> { projectDirectory, buildRoot };
        path.AddRange(extra ?? []);
        path.Add(string.Empty);

        return path.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<GitHubContentItem>> ListAsync(
        string token, string owner, string repo, string path, string? branch,
        List<string> directoriesRead, CancellationToken cancellationToken)
    {
        try
        {
            var contents = await _gitHub.ListAllContentsAsync(token, owner, repo, path, branch, cancellationToken);
            if (contents is null)
            {
                // A listing that came back empty-handed was not read, whatever the call reported.
                return [];
            }

            directoriesRead.Add(path);
            return contents;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not recorded as read: a directory that could not be listed is precisely the case
            // IsInconclusive exists to expose.
            return [];
        }
    }

    private static bool IsDirectory(GitHubContentItem item) =>
        string.Equals(item.Type, "dir", StringComparison.OrdinalIgnoreCase);

    private static string DirectoryOf(string path) =>
        path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static string Normalize(string? path) =>
        path?.Trim().Replace('\\', '/').Trim('/') ?? string.Empty;

    private static string Join(string directory, string file) =>
        string.IsNullOrEmpty(directory) ? file : $"{directory}/{file}";
}
