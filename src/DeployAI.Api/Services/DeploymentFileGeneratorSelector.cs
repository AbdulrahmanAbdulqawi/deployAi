using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>Chooses which <see cref="IDeploymentFileGenerator"/> implementation to use for a setup run.</summary>
public interface IDeploymentFileGeneratorSelector
{
    /// <summary>
    /// Selects a generator for this plan: by shape first, then by the caller's AI preference and
    /// whether an Anthropic API key is configured.
    /// </summary>
    Task<DeploymentFileGeneratorSelection> SelectAsync(
        IReadOnlyList<DeploymentPlanPart> parts,
        bool? useAi,
        Func<string, Task>? reportActivity);
}

/// <summary>The chosen generator and which mode it's running in ("ai", "template", or "template-fallback").</summary>
public sealed record DeploymentFileGeneratorSelection(
    IDeploymentFileGenerator Generator,
    string Mode);

/// <summary>
/// Chooses between the Claude-powered generator and the built-in template
/// generator based on the caller's preference and whether an Anthropic API key
/// is configured. When AI is requested but unavailable, it falls back to the
/// template generator instead of failing the setup flow.
/// </summary>
/// <remarks>
/// A single-origin compose plan is decided before any of that, by shape rather than by
/// preference. Its files are topology — which services exist, what builds them, what the proxy
/// routes where — and that is derived from the repository, not written prose-first. Leaving it to
/// the AI path is what let <c>nginx.conf</c> vanish from a commit for its file extension without
/// anything noticing; leaving it to the templates is what kept the whole shape keyed to the literal
/// framework name "angular".
/// </remarks>
public sealed class DeploymentFileGeneratorSelector : IDeploymentFileGeneratorSelector
{
    public const string AiMode = "ai";
    public const string TemplateMode = "template";
    public const string TemplateFallbackMode = "template-fallback";
    public const string GraphMode = "graph";

    private readonly HybridDeploymentFileGenerator _hybridGenerator;
    private readonly TemplateDeploymentFileGenerator _templateGenerator;
    private readonly GraphComposeFileGenerator _graphGenerator;
    private readonly AnthropicMessageClient _anthropic;
    private readonly AnthropicOptions _options;

    public DeploymentFileGeneratorSelector(
        HybridDeploymentFileGenerator hybridGenerator,
        TemplateDeploymentFileGenerator templateGenerator,
        GraphComposeFileGenerator graphGenerator,
        AnthropicMessageClient anthropic,
        IOptions<AnthropicOptions> options)
    {
        _hybridGenerator = hybridGenerator;
        _templateGenerator = templateGenerator;
        _graphGenerator = graphGenerator;
        _anthropic = anthropic;
        _options = options.Value;
    }

    public async Task<DeploymentFileGeneratorSelection> SelectAsync(
        IReadOnlyList<DeploymentPlanPart> parts,
        bool? useAi,
        Func<string, Task>? reportActivity)
    {
        if (SplitOriginDetection.PlanUsesSingleOriginCompose(parts))
        {
            await ReportAsync(
                reportActivity,
                "Building the deployment from the repository's own structure.");
            return new DeploymentFileGeneratorSelection(_graphGenerator, GraphMode);
        }

        var wantAi = useAi ?? _options.PreferAiSetup;

        if (!wantAi)
        {
            await ReportAsync(reportActivity, "Using built-in deployment templates.");
            return new DeploymentFileGeneratorSelection(_templateGenerator, TemplateMode);
        }

        if (_anthropic.IsConfigured)
        {
            return new DeploymentFileGeneratorSelection(_hybridGenerator, AiMode);
        }

        await ReportAsync(
            reportActivity,
            "Anthropic API key not configured — using built-in deployment templates.");
        return new DeploymentFileGeneratorSelection(_templateGenerator, TemplateFallbackMode);
    }

    private static Task ReportAsync(Func<string, Task>? reportActivity, string message) =>
        reportActivity is null ? Task.CompletedTask : reportActivity(message);
}
