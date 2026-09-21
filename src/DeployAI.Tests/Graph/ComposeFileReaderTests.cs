using DeployAI.Infrastructure.GitHub;

namespace DeployAI.Tests.Graph;

/// <summary>
/// Reading a compose file into services, so the graph builder can see a repository's real shape
/// rather than the two-part one every caller has assumed.
///
/// The fixtures are the two shapes on the live Coolify instance as of 2026-09-22: `mirqab`
/// (api + web, both built) and `reel-hub` (api + worker + web + a postgres service *inside* the
/// compose, with a named volume and a healthcheck). The second is the one nothing in DeployAI
/// can currently express.
/// </summary>
public class ComposeFileReaderTests
{
    private const string ReelHubCompose = """
        services:
          db:
            image: 'postgres:16-alpine'
            restart: unless-stopped
            environment:
              POSTGRES_DB: '${POSTGRES_DB:-nabdah}'
              POSTGRES_USER: '${POSTGRES_USER:-nabdah}'
              POSTGRES_PASSWORD: '${SERVICE_PASSWORD_POSTGRES}'
            volumes:
              - 'nabdah-db-data:/var/lib/postgresql/data'
            healthcheck:
              test:
                - CMD-SHELL
                - 'pg_isready -U ${POSTGRES_USER:-nabdah} -d ${POSTGRES_DB:-nabdah}'
              interval: 10s
          api:
            build:
              context: ./server
              dockerfile: Dockerfile
            environment:
              - 'ConnectionStrings__Default=${DATABASE_URL}'
              - ASPNETCORE_ENVIRONMENT=Production
            expose:
              - '8080'
            depends_on:
              - db
          worker:
            build: ./server
            command: dotnet Nabdah.Worker.dll
            depends_on:
              - db
          web:
            build: ./client
            expose:
              - '80'
            depends_on:
              - api
        volumes:
          nabdah-db-data:
        """;

    private const string MirqabCompose = """
        services:
          api:
            build: ./
            environment:
              - 'Mirqab__Operator__Email=${MIRQAB_OPERATOR_EMAIL}'
            expose:
              - '8080'
          web:
            build: ./client
            expose:
              - '80'
        """;

    private static ComposeFile Read(string content) => ComposeFileReader.Read(content);

    [Fact]
    public void Read_FindsEveryService_NotJustApiAndWeb()
    {
        var compose = Read(ReelHubCompose);

        Assert.False(compose.IsInconclusive);
        Assert.Equal(["db", "api", "worker", "web"], compose.Services.Select(s => s.Name));
    }

    /// <summary>
    /// A database declared as a compose service is not a database DeployAI should provision on the
    /// provider. Telling the two apart is the whole point of reading the file.
    /// </summary>
    [Fact]
    public void Read_DistinguishesAPrebuiltImageFromABuiltService()
    {
        var compose = Read(ReelHubCompose);

        var db = compose.Services.Single(s => s.Name == "db");
        Assert.Equal("postgres:16-alpine", db.Image);
        Assert.Null(db.BuildContext);
        Assert.True(db.HasHealthcheck);
        var volume = Assert.Single(db.Volumes);
        Assert.Equal("nabdah-db-data", volume.Name);
        Assert.Equal("/var/lib/postgresql/data", volume.MountPath);

        var api = compose.Services.Single(s => s.Name == "api");
        Assert.Equal("./server", api.BuildContext);
        Assert.Equal("Dockerfile", api.DockerfilePath);
        Assert.Null(api.Image);
        Assert.False(api.HasHealthcheck);
    }

    /// <summary>Both spellings of `build:` — the scalar shorthand and the mapping.</summary>
    [Fact]
    public void Read_AcceptsBuildAsAStringOrAMapping()
    {
        var compose = Read(ReelHubCompose);

        Assert.Equal("./server", compose.Services.Single(s => s.Name == "worker").BuildContext);
        Assert.Equal("./server", compose.Services.Single(s => s.Name == "api").BuildContext);
    }

    /// <summary>
    /// A built service with no exposed port, no ingress and a command of its own is a worker.
    /// The reader only reports the facts; the graph builder draws that conclusion.
    /// </summary>
    [Fact]
    public void Read_ReportsPortsAndCommandsPerService()
    {
        var compose = Read(ReelHubCompose);

        Assert.Equal([8080], compose.Services.Single(s => s.Name == "api").ExposedPorts);
        Assert.Equal([80], compose.Services.Single(s => s.Name == "web").ExposedPorts);

        var worker = compose.Services.Single(s => s.Name == "worker");
        Assert.Empty(worker.ExposedPorts);
        Assert.Equal("dotnet Nabdah.Worker.dll", worker.Command);
    }

    [Fact]
    public void Read_ReportsDependencies()
    {
        var compose = Read(ReelHubCompose);

        Assert.Equal(["db"], compose.Services.Single(s => s.Name == "api").DependsOn);
        Assert.Equal(["api"], compose.Services.Single(s => s.Name == "web").DependsOn);
        Assert.Empty(compose.Services.Single(s => s.Name == "db").DependsOn);
    }

    /// <summary>
    /// Environment appears as a mapping in one service and a list in another, in the same file.
    /// Both are valid compose and both carry the keys the app binds.
    /// </summary>
    [Fact]
    public void Read_ReadsEnvironmentAsEitherAMappingOrAList()
    {
        var compose = Read(ReelHubCompose);

        var db = compose.Services.Single(s => s.Name == "db");
        Assert.Contains("POSTGRES_DB", db.EnvironmentKeys);
        Assert.Contains("POSTGRES_PASSWORD", db.EnvironmentKeys);

        var api = compose.Services.Single(s => s.Name == "api");
        Assert.Contains("ConnectionStrings__Default", api.EnvironmentKeys);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", api.EnvironmentKeys);
    }

    /// <summary>
    /// The `${VAR}` references are the values the deploy has to supply. Without them the app boots
    /// with empty configuration and crash-loops, which is the incident the env step exists for.
    /// </summary>
    [Fact]
    public void Read_CollectsTheVariablesTheFileExpectsToBeSupplied()
    {
        var compose = Read(ReelHubCompose);

        Assert.Contains("DATABASE_URL", compose.ReferencedVariables);
        Assert.Contains("SERVICE_PASSWORD_POSTGRES", compose.ReferencedVariables);
        // A default does not stop it being a variable the deploy may want to set.
        Assert.Contains("POSTGRES_DB", compose.ReferencedVariables);
    }

    /// <summary>
    /// Host ports are per-service, not per-file. The regex check they replace matched any `ports:`
    /// line anywhere and so could not name the offender.
    /// </summary>
    [Fact]
    public void Read_AttributesHostPortMappingsToTheServiceThatPublishesThem()
    {
        var compose = Read("""
            services:
              api:
                build: ./server
                ports:
                  - '8080:8080'
              web:
                build: ./client
                expose:
                  - '80'
            """);

        Assert.Equal(["8080:8080"], compose.Services.Single(s => s.Name == "api").HostPortMappings);
        Assert.Empty(compose.Services.Single(s => s.Name == "web").HostPortMappings);
    }

    [Fact]
    public void Read_HandlesTheTwoServiceShape()
    {
        var compose = Read(MirqabCompose);

        Assert.Equal(["api", "web"], compose.Services.Select(s => s.Name));
        Assert.Equal("./", compose.Services.Single(s => s.Name == "api").BuildContext);
        Assert.Contains("MIRQAB_OPERATOR_EMAIL", compose.ReferencedVariables);
    }

    /// <summary>
    /// "Could not parse" is not "declares no services". A file DeployAI cannot read must never
    /// look like a file with nothing in it — the same rule as every other scan here.
    /// </summary>
    [Fact]
    public void Read_MarksUnparseableContentInconclusive()
    {
        var compose = Read("services:\n  api:\n   build: ./\n  \t- broken: [unclosed\n");

        Assert.True(compose.IsInconclusive);
        Assert.Empty(compose.Services);
        Assert.False(string.IsNullOrWhiteSpace(compose.ParseError));
    }

    [Fact]
    public void Read_ValidYamlWithNoServicesIsConclusiveAndEmpty()
    {
        var compose = Read("version: '3.9'\nvolumes:\n  data:\n");

        Assert.False(compose.IsInconclusive);
        Assert.Empty(compose.Services);
    }
}
