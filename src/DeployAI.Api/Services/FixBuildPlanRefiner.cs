using System.Text.RegularExpressions;

namespace DeployAI.Api.Services;

public static class FixBuildPlanRefiner
{
    private static readonly Regex BuildCsprojRegex = new(
        @"dotnet\s+build\s+[""']?(?<path>[^""'\s]+\.csproj)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FixBuildPlan Refine(string repoRoot, FixBuildPlan plan)
    {
        if (!plan.BuildCommand.Contains("dotnet build", StringComparison.OrdinalIgnoreCase))
        {
            return plan;
        }

        var configuredWorkingDir = plan.WorkingDirectory.Replace('\\', '/').Trim();
        var (workingDirRelative, workingDirFull) = ResolveWorkingDirectory(repoRoot, plan.WorkingDirectory);
        var csprojInCommand = ExtractCsprojFromBuildCommand(plan.BuildCommand);
        if (string.IsNullOrWhiteSpace(csprojInCommand))
        {
            return new FixBuildPlan(workingDirRelative, plan.InstallCommand, plan.BuildCommand);
        }

        var normalizedCsproj = csprojInCommand.Replace('\\', '/').Trim().TrimStart('.').TrimStart('/');
        var candidateFull = Path.GetFullPath(
            Path.Combine(workingDirFull, normalizedCsproj.Replace('/', Path.DirectorySeparatorChar)));

        if (File.Exists(candidateFull))
        {
            return PlanForProject(workingDirRelative, workingDirFull, candidateFull, plan.InstallCommand);
        }

        var fileName = Path.GetFileName(normalizedCsproj);
        var matches = Directory
            .EnumerateFiles(repoRoot, fileName, SearchOption.AllDirectories)
            .Where(path => !IsUnderGitDirectory(path))
            .ToList();

        if (matches.Count == 0)
        {
            return new FixBuildPlan(workingDirRelative, plan.InstallCommand, plan.BuildCommand);
        }

        var hint = configuredWorkingDir.Trim('/');
        if (string.IsNullOrWhiteSpace(hint) || string.Equals(hint, ".", StringComparison.Ordinal))
        {
            hint = Path.GetFileNameWithoutExtension(fileName);
        }
        else
        {
            hint = hint.Split('/').Last();
        }

        var preferred = matches.FirstOrDefault(path =>
            path.Replace('\\', '/').Contains($"/{hint}/", StringComparison.OrdinalIgnoreCase) ||
            path.Replace('\\', '/').EndsWith($"/{hint}/{fileName}", StringComparison.OrdinalIgnoreCase));

        var selected = preferred ?? (matches.Count == 1 ? matches[0] : null);
        return selected is null
            ? new FixBuildPlan(workingDirRelative, plan.InstallCommand, plan.BuildCommand)
            : PlanForProject(repoRoot, selected, plan.InstallCommand);
    }

    private static FixBuildPlan PlanForProject(
        string workingDirRelative,
        string workingDirFull,
        string projectFullPath,
        string? installCommand) =>
        new(
            workingDirRelative,
            installCommand,
            $"dotnet build \"{MakeRelativeToDirectory(workingDirFull, projectFullPath)}\"");

    private static FixBuildPlan PlanForProject(string repoRoot, string projectFullPath, string? installCommand)
    {
        var projectDirFull = Path.GetDirectoryName(projectFullPath)
            ?? throw new InvalidOperationException("Project path did not include a directory.");

        var workingDirRelative = MakeRelativeToDirectory(repoRoot, projectDirFull);
        if (string.IsNullOrWhiteSpace(workingDirRelative))
        {
            workingDirRelative = ".";
        }

        return new FixBuildPlan(
            workingDirRelative,
            installCommand,
            $"dotnet build \"{MakeRelativeToDirectory(projectDirFull, projectFullPath)}\"");
    }

    private static (string Relative, string Full) ResolveWorkingDirectory(string repoRoot, string workingDirectory)
    {
        var relative = workingDirectory.Replace('\\', '/').Trim();
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return (".", repoRoot);
        }

        relative = relative.Trim('/');
        var full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(full))
        {
            return (relative, full);
        }

        return (".", repoRoot);
    }

    private static string? ExtractCsprojFromBuildCommand(string buildCommand)
    {
        var match = BuildCsprojRegex.Match(buildCommand);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static string MakeRelativeToDirectory(string directoryFullPath, string targetFullPath) =>
        Path.GetRelativePath(directoryFullPath, targetFullPath).Replace('\\', '/');

    private static bool IsUnderGitDirectory(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
