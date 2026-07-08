using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentFileGeneratorSelectorTests
{
    [Fact]
    public async Task SelectAsync_ReturnsHybrid_WhenAiRequestedAndConfigured()
    {
        var (selector, hybrid, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(useAi: true, reportActivity: null);

        Assert.Same(hybrid, selection.Generator);
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
        var (aiSelector, hybrid, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);
        var aiSelection = await aiSelector.SelectAsync(useAi: null, reportActivity: null);
        Assert.Same(hybrid, aiSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.AiMode, aiSelection.Mode);

        var (templateSelector, _, template, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: false);
        var templateSelection = await templateSelector.SelectAsync(useAi: null, reportActivity: null);
        Assert.Same(template, templateSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateMode, templateSelection.Mode);
    }

    private static (
        DeploymentFileGeneratorSelector Selector,
        HybridDeploymentFileGenerator Hybrid,
        TemplateDeploymentFileGenerator Template,
        AnthropicMessageClient Anthropic) CreateSelector(string apiKey, bool preferAiSetup)
    {
        var options = Options.Create(new AnthropicOptions
        {
            ApiKey = apiKey,
            PreferAiSetup = preferAiSetup
        });
        var anthropic = new AnthropicMessageClient(new HttpClient(), options);
        var gitHub = new Mock<IGitHubService>().Object;
        var catalog = new DeploymentTemplateCatalog();
        var resolver = new DeploymentTemplateResolver(catalog);
        var scaffolder = new DeploymentFileScaffolder(resolver);
        var fileFetcher = new DeploymentSetupFileFetcher(gitHub);
        var hybrid = new HybridDeploymentFileGenerator(
            anthropic,
            gitHub,
            scaffolder,
            fileFetcher,
            resolver);
        var template = new TemplateDeploymentFileGenerator(scaffolder, fileFetcher);
        var selector = new DeploymentFileGeneratorSelector(hybrid, template, anthropic, options);
        return (selector, hybrid, template, anthropic);
    }
}
