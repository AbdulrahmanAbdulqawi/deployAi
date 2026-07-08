using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeployAI.Api.Services.DeploymentTemplates;

public sealed class DeploymentTemplateCatalog
{
    private const int MaxAiReferenceTemplates = 8;

    private readonly string _templatesRoot;
    private readonly Lazy<CatalogSnapshot> _snapshot;

    public DeploymentTemplateCatalog()
    {
        _templatesRoot = ResolveTemplatesRoot();
        _snapshot = new Lazy<CatalogSnapshot>(LoadCatalog);
    }

    private static string ResolveTemplatesRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(DeploymentTemplateCatalog).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            var fromAssembly = Path.Combine(assemblyDir, "DeploymentTemplates");
            if (Directory.Exists(fromAssembly))
            {
                return fromAssembly;
            }
        }

        var fromBase = Path.Combine(AppContext.BaseDirectory, "DeploymentTemplates");
        if (Directory.Exists(fromBase))
        {
            return fromBase;
        }

        throw new DirectoryNotFoundException(
            $"Deployment template directory not found. Checked: {assemblyDir}\\DeploymentTemplates and {fromBase}");
    }

    internal IReadOnlyList<DeploymentTemplateScenario> Scenarios => _snapshot.Value.Scenarios;

    internal IReadOnlyList<DeploymentTemplateDefinition> Templates => _snapshot.Value.Templates;

    internal string ReadTemplateContent(string resourcePath)
    {
        var fullPath = Path.Combine(_templatesRoot, resourcePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Deployment template not found: {resourcePath}", fullPath);
        }

        return File.ReadAllText(fullPath);
    }

    internal DeploymentTemplateDefinition? FindTemplateById(string templateId) =>
        Templates.FirstOrDefault(template => template.Id.Equals(templateId, StringComparison.Ordinal));

    internal DeploymentTemplateDefinition? FindTemplateForPath(string scenarioId, string gapPath)
    {
        var normalizedPath = NormalizePath(gapPath);
        var fileName = Path.GetFileName(normalizedPath);

        return Templates
            .Where(template => template.ScenarioId.Equals(scenarioId, StringComparison.Ordinal))
            .Where(template => normalizedPath.EndsWith("/" + template.FileName, StringComparison.OrdinalIgnoreCase) ||
                               normalizedPath.Equals(template.FileName, StringComparison.OrdinalIgnoreCase) ||
                               fileName.Equals(template.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(template => template.FileName.Length)
            .FirstOrDefault();
    }

    internal IReadOnlyList<DeploymentTemplateDefinition> FindTemplatesForScenario(string scenarioId) =>
        Templates
            .Where(template => template.ScenarioId.Equals(scenarioId, StringComparison.Ordinal))
            .ToArray();

    internal static string BuildAiReferenceSection(IReadOnlyList<ResolvedDeploymentTemplate> resolvedTemplates)
    {
        if (resolvedTemplates.Count == 0)
        {
            return string.Empty;
        }

        var prioritized = resolvedTemplates
            .Where(template => template.Kind == DeploymentTemplateKind.FullFile ||
                               !string.IsNullOrWhiteSpace(template.PatchInstructions))
            .OrderBy(template => template.AiPriority)
            .ThenBy(template => template.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAiReferenceTemplates)
            .ToArray();

        if (prioritized.Length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Reference Templates");
        builder.AppendLine();
        builder.AppendLine(
            "Use these scenario-matched references for the listed gaps. Read existing repository files first, " +
            "then adapt paths, namespaces, class names, and project structure — do not copy blindly.");
        builder.AppendLine();

        foreach (var template in prioritized)
        {
            builder.AppendLine(template.BuildAiReferenceBlock());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    internal static string BuildPreGeneratedSection(IReadOnlyList<ResolvedDeploymentTemplate> resolvedTemplates)
    {
        var preGenerated = resolvedTemplates
            .Where(template => template.Kind == DeploymentTemplateKind.FullFile &&
                               !string.IsNullOrWhiteSpace(template.RenderedContent))
            .ToArray();

        if (preGenerated.Length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("## Already Generated (deterministic)");
        builder.AppendLine();
        builder.AppendLine(
            "These full files were pre-rendered from built-in templates. Submit them unchanged unless " +
            "reading the repository shows a conflicting existing file that requires adaptation.");
        builder.AppendLine();

        foreach (var template in preGenerated)
        {
            builder.AppendLine($"### `{template.TargetPath}`");
            builder.AppendLine("```");
            builder.AppendLine(template.RenderedContent!.TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private CatalogSnapshot LoadCatalog()
    {
        var catalogPath = Path.Combine(_templatesRoot, "catalog.json");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("Deployment template catalog not found.", catalogPath);
        }

        var json = File.ReadAllText(catalogPath);
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, CatalogJsonOptions)
            ?? throw new InvalidOperationException("Deployment template catalog is empty.");

        var scenarios = document.Scenarios
            .Select(scenario => new DeploymentTemplateScenario(
                scenario.Id,
                scenario.WebsiteProvider,
                scenario.BackendProvider,
                scenario.WebsiteFramework,
                scenario.BackendFramework,
                scenario.WiringMode))
            .ToArray();

        var templates = document.Templates
            .Select(template => new DeploymentTemplateDefinition(
                template.Id,
                template.ScenarioId,
                template.FileName,
                template.Kind.Equals("patch", StringComparison.OrdinalIgnoreCase)
                    ? DeploymentTemplateKind.Patch
                    : DeploymentTemplateKind.FullFile,
                template.ResourcePath,
                new DeploymentTemplateAiReference(
                    template.AiReference?.IncludeInPrompt ?? true,
                    template.AiReference?.Priority ?? 1),
                template.Constraints ?? []))
            .ToArray();

        return new CatalogSnapshot(scenarios, templates);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CatalogSnapshot(
        IReadOnlyList<DeploymentTemplateScenario> Scenarios,
        IReadOnlyList<DeploymentTemplateDefinition> Templates);

    private sealed class CatalogDocument
    {
        public List<CatalogScenario> Scenarios { get; set; } = [];

        public List<CatalogTemplate> Templates { get; set; } = [];
    }

    private sealed class CatalogScenario
    {
        public string Id { get; set; } = string.Empty;

        public string WebsiteProvider { get; set; } = string.Empty;

        public string BackendProvider { get; set; } = string.Empty;

        public string WebsiteFramework { get; set; } = string.Empty;

        public string BackendFramework { get; set; } = string.Empty;

        public string WiringMode { get; set; } = string.Empty;
    }

    private sealed class CatalogTemplate
    {
        public string Id { get; set; } = string.Empty;

        public string ScenarioId { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string Kind { get; set; } = "full-file";

        public string ResourcePath { get; set; } = string.Empty;

        public CatalogAiReference? AiReference { get; set; }

        public List<string>? Constraints { get; set; }
    }

    private sealed class CatalogAiReference
    {
        public bool IncludeInPrompt { get; set; } = true;

        public int Priority { get; set; } = 1;
    }
}
