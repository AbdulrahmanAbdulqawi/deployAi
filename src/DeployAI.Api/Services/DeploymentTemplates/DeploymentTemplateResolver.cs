using DeployAI.Core.Deployments;

namespace DeployAI.Api.Services.DeploymentTemplates;

/// <summary>Matches a repo's parts/missing files against the template catalog's scenarios and renders the applicable templates for each gap.</summary>
public sealed class DeploymentTemplateResolver
{
    private readonly DeploymentTemplateCatalog _catalog;

    public DeploymentTemplateResolver(DeploymentTemplateCatalog catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Resolves and renders templates for every missing file, matching by scenario (provider+framework) and file name.</summary>
    internal IReadOnlyList<ResolvedDeploymentTemplate> ResolveForGaps(
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<MissingDeploymentFile> missingFiles,
        IReadOnlyDictionary<string, string?>? existingFilesByPath = null)
    {
        var scenario = ResolveScenario(parts);
        if (scenario is null)
        {
            return [];
        }

        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            return [];
        }

        var variables = DeploymentTemplateRenderer.BuildVariables(website, server);
        var resolved = new List<ResolvedDeploymentTemplate>();

        foreach (var missing in missingFiles)
        {
            if (missing.Severity is not DeploymentFileSeverity.Blocking and not DeploymentFileSeverity.Recommended)
            {
                continue;
            }

            var definition = _catalog.FindTemplateForPath(scenario.Id, missing.Path);
            if (definition is null)
            {
                continue;
            }

            string? existingContent = null;
            existingFilesByPath?.TryGetValue(missing.Path, out existingContent);

            if (definition.Kind == DeploymentTemplateKind.Patch &&
                DeploymentFileScaffolder.RequiresExistingContent(missing.Path) &&
                string.IsNullOrWhiteSpace(existingContent))
            {
                continue;
            }

            var rawContent = _catalog.ReadTemplateContent(definition.ResourcePath);
            string? renderedContent = null;
            string? patchInstructions = null;

            if (definition.Kind == DeploymentTemplateKind.FullFile)
            {
                renderedContent = DeploymentTemplateRenderer.Render(rawContent, variables);
            }
            else
            {
                patchInstructions = rawContent;
            }

            if (!definition.AiReference.IncludeInPrompt &&
                definition.Kind == DeploymentTemplateKind.Patch &&
                string.IsNullOrWhiteSpace(existingContent))
            {
                continue;
            }

            resolved.Add(new ResolvedDeploymentTemplate(
                definition.Id,
                missing.Path,
                definition.Kind,
                renderedContent,
                patchInstructions,
                definition.Constraints,
                definition.AiReference.Priority));
        }

        return resolved;
    }

    /// <summary>Resolves templates matching a fix run's referenced files (rather than a full readiness scan's missing files), for use as fix-prompt reference material.</summary>
    internal IReadOnlyList<ResolvedDeploymentTemplate> ResolveForSplitOriginFix(
        IReadOnlyList<DeploymentPlanPart> parts,
        IReadOnlyList<string> referencedFilePaths)
    {
        var scenario = ResolveScenario(parts);
        if (scenario is null)
        {
            return [];
        }

        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            return [];
        }

        var variables = DeploymentTemplateRenderer.BuildVariables(website, server);
        var resolved = new List<ResolvedDeploymentTemplate>();

        foreach (var path in referencedFilePaths)
        {
            var definition = _catalog.FindTemplateForPath(scenario.Id, path);
            if (definition is null || !definition.AiReference.IncludeInPrompt)
            {
                continue;
            }

            var rawContent = _catalog.ReadTemplateContent(definition.ResourcePath);
            resolved.Add(new ResolvedDeploymentTemplate(
                definition.Id,
                path,
                definition.Kind,
                definition.Kind == DeploymentTemplateKind.FullFile
                    ? DeploymentTemplateRenderer.Render(rawContent, variables)
                    : null,
                definition.Kind == DeploymentTemplateKind.Patch ? rawContent : null,
                definition.Constraints,
                definition.AiReference.Priority));
        }

        return resolved;
    }

    /// <summary>Resolves just the matching scenario id for a plan, without resolving/rendering any templates.</summary>
    internal string? ResolveScenarioId(IReadOnlyList<DeploymentPlanPart> parts) =>
        ResolveScenario(parts)?.Id;

    private DeploymentTemplateScenario? ResolveScenario(IReadOnlyList<DeploymentPlanPart> parts)
    {
        if (!SplitOriginDetection.PlanUsesSplitOrigin(parts))
        {
            return null;
        }

        var website = SplitOriginDetection.FindWebsitePart(parts);
        var server = SplitOriginDetection.FindServerPart(parts);
        if (website is null || server is null)
        {
            return null;
        }

        var wiringMode = CrossProviderUrlWiring.ResolveWiringMode(website.Framework, server.Framework);
        if (wiringMode != CrossProviderWiringMode.SplitOrigin)
        {
            return null;
        }

        return _catalog.Scenarios.FirstOrDefault(scenario =>
            ProviderMatches(scenario.WebsiteProvider, website.ProviderName) &&
            ProviderMatches(scenario.BackendProvider, server.ProviderName) &&
            FrameworkMatches(scenario.WebsiteFramework, website.Framework) &&
            FrameworkMatches(scenario.BackendFramework, server.Framework) &&
            scenario.WiringMode.Equals("split-origin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProviderMatches(string expected, string? actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool FrameworkMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var normalizedActual = actual.ToLowerInvariant();
        var normalizedExpected = expected.ToLowerInvariant();

        return normalizedExpected switch
        {
            "angular" => normalizedActual is "angular",
            "dotnet" => normalizedActual is "dotnet" or "aspnet" or "aspnetcore" or "docker",
            _ => normalizedActual == normalizedExpected
        };
    }
}
