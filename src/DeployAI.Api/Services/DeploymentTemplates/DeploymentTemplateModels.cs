namespace DeployAI.Api.Services.DeploymentTemplates;

/// <summary>Whether a template produces an entire file's content, or a set of patch instructions to apply to an existing file.</summary>
internal enum DeploymentTemplateKind
{
    FullFile,
    Patch
}

/// <summary>Whether/how prominently a template should be included as reference material in a Claude prompt.</summary>
internal sealed record DeploymentTemplateAiReference(
    bool IncludeInPrompt,
    int Priority = 1);

/// <summary>A registered template: which scenario it applies to, where its source lives, and how it should be surfaced to Claude.</summary>
internal sealed record DeploymentTemplateDefinition(
    string Id,
    string ScenarioId,
    string FileName,
    DeploymentTemplateKind Kind,
    string ResourcePath,
    DeploymentTemplateAiReference AiReference,
    IReadOnlyList<string> Constraints);

/// <summary>A deployment shape (provider + framework combination) that templates are grouped by.</summary>
internal sealed record DeploymentTemplateScenario(
    string Id,
    string WebsiteProvider,
    string BackendProvider,
    string WebsiteFramework,
    string BackendFramework,
    string WiringMode);

/// <summary>The repo-specific values (paths, project name, API env keys) substituted into a template's placeholders when rendering it.</summary>
internal sealed record DeploymentTemplateVariables(
    string ClientRoot,
    string ClientPrefix,
    string ServerRoot,
    string DockerfilePath,
    string OutputDirectory,
    string ProjectName,
    string BuildCommand,
    string ApiEnvKeysList,
    string ApiEnvKeysExpression,
    string ServerNamespace);

/// <summary>A template already rendered/resolved for one target file, ready to either write directly (FullFile) or format as prompt reference material.</summary>
internal sealed record ResolvedDeploymentTemplate(
    string TemplateId,
    string TargetPath,
    DeploymentTemplateKind Kind,
    string? RenderedContent,
    string? PatchInstructions,
    IReadOnlyList<string> Constraints,
    int AiPriority)
{
    /// <summary>Formats this template as a markdown reference block (constraints + example content/patch instructions) to embed in a Claude prompt.</summary>
    public string BuildAiReferenceBlock()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"### Reference: `{TargetPath}` (template `{TemplateId}`)");
        builder.AppendLine();

        if (Constraints.Count > 0)
        {
            builder.AppendLine("**Constraints:**");
            foreach (var constraint in Constraints)
            {
                builder.AppendLine($"- {constraint}");
            }

            builder.AppendLine();
        }

        if (Kind == DeploymentTemplateKind.FullFile && !string.IsNullOrWhiteSpace(RenderedContent))
        {
            builder.AppendLine("**Example file (adapt paths, namespaces, and names to match the repository):**");
            builder.AppendLine("```");
            builder.AppendLine(RenderedContent.TrimEnd());
            builder.AppendLine("```");
        }
        else if (Kind == DeploymentTemplateKind.Patch && !string.IsNullOrWhiteSpace(PatchInstructions))
        {
            builder.AppendLine(PatchInstructions.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }
}
