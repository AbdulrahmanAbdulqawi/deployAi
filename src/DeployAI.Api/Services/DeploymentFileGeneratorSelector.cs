using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Api.Services;

/// <summary>Chooses which <see cref="IDeploymentFileGenerator"/> implementation to use for a setup run.</summary>
public interface IDeploymentFileGeneratorSelector
{
    /// <summary>Selects a generator based on the caller's AI preference and Anthropic API key availability.</summary>
    Task<DeploymentFileGeneratorSelection> SelectAsync(
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
public sealed class DeploymentFileGeneratorSelector : IDeploymentFileGeneratorSelector
{
    public const string AiMode = "ai";
    public const string TemplateMode = "template";
    public const string TemplateFallbackMode = "template-fallback";

    private readonly HybridDeploymentFileGenerator _hybridGenerator;
    private readonly TemplateDeploymentFileGenerator _templateGenerator;
    private readonly AnthropicMessageClient _anthropic;
    private readonly AnthropicOptions _options;

    public DeploymentFileGeneratorSelector(
        HybridDeploymentFileGenerator hybridGenerator,
        TemplateDeploymentFileGenerator templateGenerator,
        AnthropicMessageClient anthropic,
        IOptions<AnthropicOptions> options)
    {
        _hybridGenerator = hybridGenerator;
        _templateGenerator = templateGenerator;
        _anthropic = anthropic;
        _options = options.Value;
    }

    public async Task<DeploymentFileGeneratorSelection> SelectAsync(
        bool? useAi,
        Func<string, Task>? reportActivity)
    {
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
