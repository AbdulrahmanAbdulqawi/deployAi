using DeployAI.Api.Services;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentFileGeneratorSelectorTests
{
    [Fact]
    public async Task SelectAsync_ReturnsClaude_WhenAiRequestedAndConfigured()
    {
        var (selector, claude, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(useAi: true, reportActivity: null);

        Assert.Same(claude, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.AiMode, selection.Mode);
    }

    [Fact]
    public async Task SelectAsync_FallsBackToTemplate_WhenAiRequestedButNotConfigured()
    {
        var (selector, _, template, _) = CreateSelector(apiKey: "", preferAiSetup: true);
        var messages = new List<string>();

        var selection = await selector.SelectAsync(
            useAi: true,
            reportActivity: message =>
            {
                messages.Add(message);
                return Task.CompletedTask;
            });

        Assert.Same(template, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateFallbackMode, selection.Mode);
        Assert.Contains(messages, m => m.Contains("not configured", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SelectAsync_ReturnsTemplate_WhenAiDisabled()
    {
        var (selector, _, template, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(useAi: false, reportActivity: null);

        Assert.Same(template, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateMode, selection.Mode);
    }

    [Fact]
    public async Task SelectAsync_UsesServerDefault_WhenPreferenceUnset()
    {
        var (aiSelector, claude, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);
        var aiSelection = await aiSelector.SelectAsync(useAi: null, reportActivity: null);
        Assert.Same(claude, aiSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.AiMode, aiSelection.Mode);

        var (templateSelector, _, template, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: false);
        var templateSelection = await templateSelector.SelectAsync(useAi: null, reportActivity: null);
        Assert.Same(template, templateSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateMode, templateSelection.Mode);
    }

    private static (
        DeploymentFileGeneratorSelector Selector,
        ClaudeDeploymentFileGenerator Claude,
        TemplateDeploymentFileGenerator Template,
        AnthropicMessageClient Anthropic) CreateSelector(string apiKey, bool preferAiSetup)
    {
        var options = Options.Create(new AnthropicOptions
        {
            ApiKey = apiKey,
            PreferAiSetup = preferAiSetup
        });
        var anthropic = new AnthropicMessageClient(new HttpClient(), options);
        var claude = new ClaudeDeploymentFileGenerator(anthropic, new Mock<IGitHubService>().Object);
        var template = new TemplateDeploymentFileGenerator();
        var selector = new DeploymentFileGeneratorSelector(claude, template, anthropic, options);
        return (selector, claude, template, anthropic);
    }
}
