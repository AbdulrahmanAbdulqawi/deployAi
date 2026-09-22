using DeployAI.Api.Services;
using DeployAI.Api.Services.DeploymentTemplates;
using DeployAI.Core.Deployments;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Moq;

namespace DeployAI.Tests.Services;

public class DeploymentFileGeneratorSelectorTests
{
    /// <summary>A Vercel site with a Railway server — split origin, so the AI/template choice applies.</summary>
    private static readonly DeploymentPlanPart[] SplitOriginParts =
    [
        new("website", "vercel", RootDirectory: "client", Framework: "angular"),
        new("server", "railway", RootDirectory: "server", Framework: "dotnet")
    ];

    /// <summary>Both halves on one Coolify server — one compose resource, one origin.</summary>
    private static readonly DeploymentPlanPart[] ComposeParts =
    [
        new("website", "coolify", RootDirectory: "client", Framework: "angular"),
        new("server", "coolify", RootDirectory: "server", Framework: "dotnet")
    ];

    [Fact]
    public async Task SelectAsync_ReturnsHybrid_WhenAiRequestedAndConfigured()
    {
        var (selector, hybrid, _, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(SplitOriginParts, useAi: true, reportActivity: null);

        Assert.Same(hybrid, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.AiMode, selection.Mode);
    }

    [Fact]
    public async Task SelectAsync_FallsBackToTemplate_WhenAiRequestedButNotConfigured()
    {
        var (selector, _, template, _, _) = CreateSelector(apiKey: "", preferAiSetup: true);
        var messages = new List<string>();

        var selection = await selector.SelectAsync(
            SplitOriginParts,
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
        var (selector, _, template, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(SplitOriginParts, useAi: false, reportActivity: null);

        Assert.Same(template, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateMode, selection.Mode);
    }

    [Fact]
    public async Task SelectAsync_UsesServerDefault_WhenPreferenceUnset()
    {
        var (aiSelector, hybrid, _, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);
        var aiSelection = await aiSelector.SelectAsync(SplitOriginParts, useAi: null, reportActivity: null);
        Assert.Same(hybrid, aiSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.AiMode, aiSelection.Mode);

        var (templateSelector, _, template, _, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: false);
        var templateSelection = await templateSelector.SelectAsync(SplitOriginParts, useAi: null, reportActivity: null);
        Assert.Same(template, templateSelection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.TemplateMode, templateSelection.Mode);
    }

    /// <summary>
    /// A compose deployment's files are topology — which services exist, what builds them, where
    /// the proxy sends traffic — and that is read off the repository, not written prose-first.
    /// Both of the other paths have cost this shape a file: the AI path dropped <c>nginx.conf</c>
    /// for its extension, and the template path can only produce the literal framework pair it has
    /// templates for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task SelectAsync_BuildsAComposePlanFromTheGraph_WhateverTheAiPreference(bool? useAi)
    {
        var (selector, _, _, graph, _) = CreateSelector(apiKey: "sk-test", preferAiSetup: true);

        var selection = await selector.SelectAsync(ComposeParts, useAi, reportActivity: null);

        Assert.Same(graph, selection.Generator);
        Assert.Equal(DeploymentFileGeneratorSelector.GraphMode, selection.Mode);
    }

    private static (
        DeploymentFileGeneratorSelector Selector,
        HybridDeploymentFileGenerator Hybrid,
        TemplateDeploymentFileGenerator Template,
        GraphComposeFileGenerator Graph,
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
        var graph = new GraphComposeFileGenerator(
            new ComposeSignalsReader(gitHub, new DotnetProjectLocator(gitHub)),
            new FrameworkAdapterFactory([new AngularAdapter(), new DotnetAdapter()]),
            template);
        var selector = new DeploymentFileGeneratorSelector(hybrid, template, graph, anthropic, options);
        return (selector, hybrid, template, graph, anthropic);
    }
}
