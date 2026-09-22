using DeployAI.Core.Deployments.Adapters;
using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Adapters;

/// <summary>
/// The .NET project that cannot build from its own folder.
///
/// A multi-project solution's API references its siblings — TicketHub.Server needs
/// TicketHub.Business, .Data and .Domain — so <c>docker build</c> has to run from the repository
/// root to see them all. The root holds no csproj, so nothing recognised it, nothing generated a
/// Dockerfile, and a setup run produced three of the four files the deployment needed. DeployAI
/// said so rather than reporting success, which is the honest failure — but the file it could not
/// write is one it has everything it needs to write.
/// </summary>
public class DotnetRootContextTests
{
    private const string WebCsproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
          <ItemGroup><ProjectReference Include="..\TicketHub.Business\TicketHub.Business.csproj" /></ItemGroup>
        </Project>
        """;

    private static RepositorySignals RootContext() =>
        new(
            Directory: string.Empty,
            CsprojContent: WebCsproj,
            CsprojFileName: "TicketHub.Server.csproj",
            ProjectFilePath: "TicketHub.Server/TicketHub.Server.csproj");

    [Fact]
    public void ASolutionsApi_IsStillClaimedByDotnet()
    {
        var detection = new DotnetAdapter().Detect(RootContext());

        Assert.NotNull(detection);
    }

    /// <summary>
    /// Restore and publish name the project, not the directory. `dotnet restore` at a root that
    /// holds a solution would restore every project in it — tests, worker, AppHost — and publish
    /// would have nothing to pick.
    /// </summary>
    [Fact]
    public void TheBuildNamesTheProjectToPublish_NotTheDirectory()
    {
        var dockerfile = new DotnetAdapter().CreateServiceNode("api", RootContext()).Source.Dockerfile!.Content!;

        Assert.Contains("dotnet restore \"TicketHub.Server/TicketHub.Server.csproj\"", dockerfile);
        Assert.Contains("dotnet publish \"TicketHub.Server/TicketHub.Server.csproj\"", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"TicketHub.Server.dll\"]", dockerfile);
    }

    /// <summary>
    /// The whole context is copied before restore. The single-project image copies its csproj
    /// first so a restore layer survives a source edit, and that trick cannot work here: which
    /// files the restore needs is exactly what the project graph decides, and copying a guess
    /// produces a build that fails on a missing reference.
    /// </summary>
    [Fact]
    public void TheWholeContextIsCopiedBeforeRestore_SoSiblingProjectsAreThere()
    {
        var dockerfile = new DotnetAdapter().CreateServiceNode("api", RootContext()).Source.Dockerfile!.Content!;

        var copyAll = dockerfile.IndexOf("COPY . .", StringComparison.Ordinal);
        var restore = dockerfile.IndexOf("dotnet restore", StringComparison.Ordinal);

        Assert.True(copyAll >= 0, "The build context is never copied, so no sibling project is present.");
        Assert.True(copyAll < restore, "Restore runs before the sibling projects it references are copied in.");
    }

    /// <summary>
    /// A project that does sit in its own build context keeps the image it already had — that one
    /// caches its restore layer, and every app deployed from it builds this way today.
    /// </summary>
    [Fact]
    public void AProjectInItsOwnDirectory_IsUnchanged()
    {
        var dockerfile = new DotnetAdapter()
            .CreateServiceNode("api", new RepositorySignals(
                "server",
                CsprojContent: WebCsproj,
                CsprojFileName: "Breeze.Api.csproj"))
            .Source.Dockerfile!.Content!;

        Assert.Contains("COPY *.csproj .", dockerfile);
        Assert.DoesNotContain("dotnet restore \"", dockerfile);
    }
}
