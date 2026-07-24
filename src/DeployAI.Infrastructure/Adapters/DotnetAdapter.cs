using DeployAI.Core.Deployments.Adapters;
using DeployAI.Core.Deployments.Graph;
using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// ASP.NET Core as a plugin. Wraps the csproj knowledge in <see cref="CsprojBuildAnalyzer"/>
/// and generates the multi-stage Dockerfile that previously lived in Dockerfile.api.tpl.
/// </summary>
public sealed class DotnetAdapter : IFrameworkAdapter
{
    public string Id => "dotnet";

    public string? HealthPath => "/api/health";

    public EnvKeyConvention EnvConvention => EnvKeyConvention.DotnetSectionKey;

    /// <summary>The ASP.NET Core in-container default, and what nginx proxies to.</summary>
    public const int ContainerPort = 8080;

    public AdapterDetection? Detect(RepositorySignals signals)
    {
        if (string.IsNullOrWhiteSpace(signals.CsprojContent))
        {
            return null;
        }

        // A csproj is as unambiguous as it gets. A repo-provided Dockerfile alongside it does
        // not lower confidence — the node just uses that Dockerfile instead of generating one.
        return new AdapterDetection(0.95, ServiceCapability.HttpService);
    }

    public ServiceNode CreateServiceNode(string serviceId, RepositorySignals signals)
    {
        var assemblyName = ResolveAssemblyName(signals);
        var targetSdkMajor = ResolveTargetFrameworkMajor(signals.CsprojContent) ?? 8;

        DockerfileSpec dockerfile;
        if (signals.HasDockerfile)
        {
            // The repo brought its own; respect it rather than generating a competing one.
            dockerfile = new DockerfileSpec(
                Content: null,
                ExistingPath: "Dockerfile",
                ExposedPort: ContainerPort);
        }
        else
        {
            var content = $"""
                # Built with ./{signals.Directory} as the context; COPY paths are relative to it.
                FROM mcr.microsoft.com/dotnet/sdk:{targetSdkMajor}.0 AS build
                WORKDIR /src
                COPY *.csproj .
                RUN dotnet restore
                COPY . .
                RUN dotnet publish -c Release -o /app

                FROM mcr.microsoft.com/dotnet/aspnet:{targetSdkMajor}.0
                WORKDIR /app
                COPY --from=build /app .
                ENV ASPNETCORE_URLS=http://+:{ContainerPort}
                EXPOSE {ContainerPort}
                ENTRYPOINT ["dotnet", "{assemblyName}.dll"]
                """;

            dockerfile = new DockerfileSpec(content, ExistingPath: null, ExposedPort: ContainerPort);
        }

        return new ServiceNode(
            serviceId,
            ServiceCapability.HttpService,
            ServiceSource.FromBuild(signals.Directory, dockerfile),
            Framework: Id,
            Ports: [ContainerPort],
            HealthPath: HealthPath);
    }

    /// <summary>
    /// Convention: assembly name matches the csproj file name. The csproj file name beats the
    /// directory name because monorepos often nest ("server/YemeniBreeze.Api/YemeniBreeze.Api.csproj").
    /// </summary>
    private static string ResolveAssemblyName(RepositorySignals signals)
    {
        if (!string.IsNullOrWhiteSpace(signals.CsprojFileName))
        {
            var name = Path.GetFileNameWithoutExtension(signals.CsprojFileName);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        var segment = signals.Directory
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(s => !s.Equals("src", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(segment) ? "Api" : segment;
    }

    /// <summary>Reads net8.0/net10.0 from the csproj so image tags match the code's runtime.</summary>
    internal static int? ResolveTargetFrameworkMajor(string? csprojContent)
    {
        if (string.IsNullOrWhiteSpace(csprojContent))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            csprojContent,
            @"<TargetFramework>\s*net(\d+)\.\d+\s*</TargetFramework>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && int.TryParse(match.Groups[1].Value, out var major) ? major : null;
    }
}
