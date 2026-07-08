namespace DeployAI.Api.Services.DeploymentTemplates;

internal static class DeploymentTemplateRenderer
{
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
        return rendered;
    }

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

        return new DeploymentTemplateVariables(
            clientRoot,
            clientPrefix,
            serverRoot,
            dockerfilePath,
            outputDirectory,
            projectName,
            buildCommand,
            apiEnvKeysList,
            apiEnvKeysExpression);
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
