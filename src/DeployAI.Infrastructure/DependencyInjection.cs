using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Security;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.Auth;
using DeployAI.Infrastructure.Email;
using DeployAI.Infrastructure.GitHub;
using DeployAI.Infrastructure.Vercel;
using DeployAI.Infrastructure.Options;
using DeployAI.Infrastructure.Railway;
using DeployAI.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeployAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.SectionName));
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.Configure<VercelOptions>(configuration.GetSection(VercelOptions.SectionName));
        services.Configure<RailwayOptions>(configuration.GetSection(RailwayOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddSingleton<IEncryptionService, AesEncryptionService>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<DeployAI.Infrastructure.Email.IEmailSender, DeployAI.Infrastructure.Email.SmtpEmailSender>();
        services.AddHttpClient<IGitHubService, GitHubService>();
        services.AddHttpClient<IVercelOAuthService, VercelOAuthService>();
        services.AddHttpClient<IRailwayOAuthService, RailwayOAuthService>();
        services.AddSingleton<IFrontendBuildDetector, FrontendBuildDetector>();
        services.AddSingleton<IServerBuildDetector, ServerBuildDetector>();
        services.AddSingleton<IDatabaseRequirementDetector, DatabaseRequirementDetector>();
        services.AddSingleton<IEnvVarDetector, EnvVarDetector>();
        services.AddScoped<IServerBuildProfileDiscovery, ServerBuildProfileDiscovery>();
        services.AddScoped<IWebsiteBuildProfileDiscovery, WebsiteBuildProfileDiscovery>();
        services.AddScoped<IRepositoryClassifier, RepositoryClassifier>();

        // Framework adapters: everything framework-specific lives behind IFrameworkAdapter, so
        // adding a stack is one class plus one line here — shapes/planner/generators unchanged.
        services.AddSingleton<IFrameworkAdapter, AngularAdapter>();
        services.AddSingleton<IFrameworkAdapter, DotnetAdapter>();
        services.AddSingleton<IFrameworkAdapter, NodeExpressAdapter>();
        services.AddSingleton<IFrameworkAdapterFactory, FrameworkAdapterFactory>();

        return services;
    }
}
