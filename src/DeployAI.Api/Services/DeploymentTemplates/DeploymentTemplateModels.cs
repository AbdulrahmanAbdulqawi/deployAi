namespace DeployAI.Api.Services.DeploymentTemplates;

internal enum DeploymentTemplateKind
{
    FullFile,
    Patch
}

internal sealed record DeploymentTemplateAiReference(
    bool IncludeInPrompt,
    int Priority = 1);

internal sealed record DeploymentTemplateDefinition(
    string Id,
    string ScenarioId,
    string FileName,
    DeploymentTemplateKind Kind,
    string ResourcePath,
    DeploymentTemplateAiReference AiReference,
    IReadOnlyList<string> Constraints);

internal sealed record DeploymentTemplateScenario(
    string Id,
    string WebsiteProvider,
    string BackendProvider,
    string WebsiteFramework,
    string BackendFramework,
    string WiringMode);

internal sealed record DeploymentTemplateVariables(
    string ClientRoot,
    string ClientPrefix,
    string ServerRoot,
    string DockerfilePath,
    string OutputDirectory,
    string ProjectName,
    string BuildCommand,
    string ApiEnvKeysList,
    string ApiEnvKeysExpression);

internal sealed record ResolvedDeploymentTemplate(
    string TemplateId,
    string TargetPath,
    DeploymentTemplateKind Kind,
    string? RenderedContent,
    string? PatchInstructions,
    IReadOnlyList<string> Constraints,
    int AiPriority)
{
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
