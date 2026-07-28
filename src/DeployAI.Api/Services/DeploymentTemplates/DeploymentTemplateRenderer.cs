namespace DeployAI.Api.Services.DeploymentTemplates;

/// <summary>Substitutes <c>{{placeholder}}</c> tokens in template file content with repo-specific values.</summary>
internal static class DeploymentTemplateRenderer
{
    /// <summary>Replaces every known placeholder token in template content with the corresponding variable value.</summary>
    internal static string Render(string templateContent, DeploymentTemplateVariables variables)
    {
        var rendered = templateContent;
        rendered = rendered.Replace("{{clientRoot}}", variables.ClientRoot, StringComparison.Ordinal);
        rendered = rendered.Replace("{{clientPrefix}}", variables.ClientPrefix, StringComparison.Ordinal);
        rendered = rendered.Replace("{{serverRoot}}", variables.ServerRoot, StringComparison.Ordinal);
        rendered = rendered.Replace("{{dockerfilePath}}", variables.DockerfilePath, StringComparison.Ordinal);
        rendered = rendered.Replace("{{outputDirectory}}", variables.OutputDirectory, StringComparison.Ordinal);
        rendered = rendered.Replace("{{projectName}}", variables.ProjectName, StringComparison.Ordinal);
        rendered = rendered.Replace("{{buildCommand}}", variables.BuildCommand, StringComparison.Ordinal);
        rendered = rendered.Replace("{{apiEnvKeysList}}", variables.ApiEnvKeysList, StringComparison.Ordinal);
        rendered = rendered.Replace("{{apiEnvKeysExpression}}", variables.ApiEnvKeysExpression, StringComparison.Ordinal);
        rendered = rendered.Replace("{{serverNamespace}}", variables.ServerNamespace, StringComparison.Ordinal);
        rendered = rendered.Replace("{{rawBuildCommand}}", variables.RawBuildCommand, StringComparison.Ordinal);
        rendered = rendered.Replace("{{serverAssemblyName}}", variables.ServerAssemblyName, StringComparison.Ordinal);
        return rendered;
    }

    /// <summary>Derives template variables (roots, docker path, project/namespace names) from a website+server part pair.</summary>
    internal static DeploymentTemplateVariables BuildVariables(
        DeployAI.Core.Deployments.DeploymentPlanPart website,
        DeployAI.Core.Deployments.DeploymentPlanPart server)
    {
        var clientRoot = NormalizeRoot(website.RootDirectory);
        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";
        var serverRoot = NormalizeRoot(server.ServiceDirectory ?? server.RootDirectory);
        var dockerfilePath = server.DockerfilePath?.Replace('\\', '/').TrimStart('/') ??
                             $"{serverRoot}/Dockerfile";
        var outputDirectory = website.OutputDirectory?.Trim().Trim('/') ?? "dist/app/browser";
        var projectName = GuessProjectName(clientRoot);
        var buildCommand = ResolveBuildCommand(website);
        var apiEnvKeys = CrossProviderUrlWiring.ResolveApiEnvKeys(website.Framework);
        var apiEnvKeysList = string.Join(" / ", apiEnvKeys);
        var apiEnvKeysExpression = string.Join(" ?? ", apiEnvKeys.Select(key => $"process.env.{key}"));
        var serverNamespace = GuessServerNamespace(serverRoot);

        return new DeploymentTemplateVariables(
            clientRoot,
            clientPrefix,
            serverRoot,
            dockerfilePath,
            outputDirectory,
            projectName,
            buildCommand,
            apiEnvKeysList,
            apiEnvKeysExpression,
            serverNamespace,
            string.IsNullOrWhiteSpace(website.BuildCommand) ? "npm run build" : website.BuildCommand.Trim(),
            GuessServerAssemblyName(serverRoot));
    }

    /// <summary>
    /// .NET's convention is that the project directory, the csproj, and the output assembly share
    /// a name, so the server root's last segment is the best guess available without reading the
    /// csproj. The compose Dockerfile template carries a constraint telling the generator to
    /// correct it when the repo breaks that convention.
    /// </summary>
    internal static string GuessServerAssemblyName(string serverRoot)
    {
        var segment = serverRoot
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.Equals("src", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(segment) ? "Api" : segment;
    }

    /// <summary>
    /// Best-effort default namespace for generated controllers, derived from the server's
    /// root directory name rather than hardcoding the tool's own name (avoids colliding
    /// with or looking out of place among the target repo's real namespace conventions).
    /// </summary>
    internal static string GuessServerNamespace(string serverRoot)
    {
        var segment = serverRoot
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.Equals("src", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(segment))
        {
            return "Api.Controllers";
        }

        var pascal = string.Concat(
            segment.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

        return string.IsNullOrWhiteSpace(pascal) ? "Api.Controllers" : $"{pascal}.Controllers";
    }

    private static string ResolveBuildCommand(DeployAI.Core.Deployments.DeploymentPlanPart website)
    {
        if (string.IsNullOrWhiteSpace(website.BuildCommand))
        {
            return "npm ci && node scripts/write-api-env.mjs && npm run build";
        }

        return website.BuildCommand.Contains("write-api-env", StringComparison.OrdinalIgnoreCase)
            ? website.BuildCommand
            : $"npm ci && node scripts/write-api-env.mjs && {website.BuildCommand}";
    }

    private static string GuessProjectName(string clientRoot)
    {
        if (string.IsNullOrWhiteSpace(clientRoot))
        {
            return "app";
        }

        var segment = clientRoot.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "app";
        return segment.Replace(".client", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string? root) =>
        root?.Trim().Trim('/') ?? string.Empty;
}
