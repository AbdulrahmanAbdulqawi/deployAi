using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Domains;
using DeployAI.Core.Security;
using DeployAI.Infrastructure.Adapters;
using DeployAI.Infrastructure.Auth;
using DeployAI.Infrastructure.Dns;
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
        // One instance serves both interfaces: resolving a layout and reading through it are the same
        // component, and splitting them would let a caller read against a layout from a different
        // resolver. Registered here rather than in the API so the scanners that live in this
        // assembly can take it — the whole point is that nobody derives the layout independently.
        services.AddScoped<RepositoryLayoutResolver>();
        services.AddScoped<IRepositoryLayoutResolver>(sp => sp.GetRequiredService<RepositoryLayoutResolver>());
        services.AddScoped<IRepositoryReader>(sp => sp.GetRequiredService<RepositoryLayoutResolver>());

        services.AddScoped<IServerBuildProfileDiscovery, ServerBuildProfileDiscovery>();
        services.AddScoped<IWebsiteBuildProfileDiscovery, WebsiteBuildProfileDiscovery>();
        services.AddScoped<IRepositoryClassifier, RepositoryClassifier>();

        // The two probes a domain is proved with: where the name points, and what certificate the
        // name is served. Both report an invalid or absent answer rather than throwing, so a
        // caller can tell "could not check" from "checked, and it is wrong".
        services.AddSingleton<IDnsResolver, DnsClientResolver>();
        services.AddSingleton<ICertificateInspector, SslStreamCertificateInspector>();

        // Framework adapters: everything framework-specific lives behind IFrameworkAdapter, so
        // adding a stack is one class plus one line here — shapes/planner/generators unchanged.
        services.AddSingleton<IFrameworkAdapter, AngularAdapter>();
        services.AddSingleton<IFrameworkAdapter, DotnetAdapter>();
        services.AddSingleton<IFrameworkAdapter, NodeExpressAdapter>();
        services.AddSingleton<IFrameworkAdapterFactory, FrameworkAdapterFactory>();

        return services;
    }
}
