using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using DeployAI.Core.Security;
using DeployAI.Data;
using DeployAI.Data.Entities;
using DeployAI.Infrastructure.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// Three incidents in one day were this gap, all found by something breaking rather than by a check:
/// an API crash-looped on "Jwt configuration missing" until Coolify gave up at ten attempts; a merge
/// that introduced a Media module needing Storage:* took every route down; and Tickets:SigningKey
/// 500'd every Events request while the app looked healthy. The build was green and the migration
/// chain valid in all three — the fault was between the code and the target, where nothing looked.
/// </summary>
public class RequiredConfigurationCheckTests
{
    private const string Owner = "tester";
    private const string Repo = "yemenConnect";
    private const string Branch = "main";

    /// <summary>yemenConnect's own shape: empty leaves are keys the app expects to be given.</summary>
    private const string Appsettings = """
        {
          "Jwt": { "SigningKey": "" },
          "Tickets": { "SigningKey": "" },
          "Storage": { "Endpoint": "", "Bucket": "yemenhub-media" },
          "Tenancy": { "DefaultTenantId": "01900000-0000-7000-8000-000000000001" }
        }
        """;

    [Fact]
    public async Task NamesTheSettingsDeclaredWithNoValueThatTheAppDoesNotHave()
    {
        // The exact three, against an app that has none of them.
        // Worded as "declares", not "needs": the first real run reported a setting the app defines
        // and never reads, so a prediction of failure would have been wrong the very first time.
        var result = await Check(present: []);

        Assert.False(result.Inconclusive);
        Assert.Contains("Jwt__SigningKey", result.Missing);
        Assert.Contains("Tickets__SigningKey", result.Missing);
        Assert.Contains("Storage__Endpoint", result.Missing);
        Assert.Contains("if the app reads any of them, it will fail", result.Message);
    }

    [Fact]
    public async Task IgnoresSettingsThatAlreadyCarryTheirOwnValue()
    {
        // Storage__Bucket and Tenancy__DefaultTenantId have values in the file, so the app has an
        // answer already. Warning about them is noise, and noise trains people to ignore the check.
        var result = await Check(present: []);

        Assert.DoesNotContain("Storage__Bucket", result.Missing);
        Assert.DoesNotContain("Tenancy__DefaultTenantId", result.Missing);
    }

    [Fact]
    public async Task SaysNothingIsMissingWhenTheAppHasEverything()
    {
        var result = await Check(present: ["Jwt__SigningKey", "Tickets__SigningKey", "Storage__Endpoint"]);

        Assert.Empty(result.Missing);
        Assert.False(result.Inconclusive);
        Assert.Contains("all present", result.Message);
    }

    [Fact]
    public async Task MatchesKeyNamesWithoutCaringAboutCase()
    {
        var result = await Check(present: ["JWT__SIGNINGKEY", "tickets__signingkey", "storage__endpoint"]);

        Assert.Empty(result.Missing);
    }

    [Fact]
    public async Task DoesNotAskForSettingsTheRuntimeSupplies()
    {
        var result = await Check(
            present: [],
            appsettings: """{ "ASPNETCORE_URLS": "", "PORT": "", "Jwt": { "SigningKey": "" } }""");

        Assert.DoesNotContain("ASPNETCORE_URLS", result.Missing);
        Assert.DoesNotContain("PORT", result.Missing);
    }

    /// <summary>
    /// yemenConnect's real pair of files. appsettings.json has no Jwt, Tickets or Bootstrap section
    /// at all — those live only in the Development file — which is why the check built to catch a
    /// crash-loop on "Jwt configuration missing" said nothing about Jwt on the very repository that
    /// crash-looped.
    /// </summary>
    private const string BaseAppsettings = """
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "ConnectionStrings": { "Postgres": "Host=localhost;Database=yemenhub" },
          "Storage": { "Endpoint": "", "Bucket": "yemenhub-media" }
        }
        """;

    private const string DevelopmentAppsettings = """
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "ConnectionStrings": { "Postgres": "Host=localhost;Database=yemenhub" },
          "Jwt": { "Issuer": "yemenhub-dev", "SigningKey": "dev-only-key" },
          "Tickets": { "SigningKey": "dev-only-ticket-key" },
          "Storage": { "Endpoint": "http://localhost:9000", "Bucket": "yemenhub-media" }
        }
        """;

    [Fact]
    public async Task NamesASectionConfiguredOnlyForDevelopmentThatTheAppHasNothingFrom()
    {
        var result = await Check(
            present: ["Storage__Endpoint"],
            appsettings: BaseAppsettings,
            developmentAppsettings: DevelopmentAppsettings);

        Assert.Contains("Jwt", result.UnconfiguredSections!);
        Assert.Contains("Tickets", result.UnconfiguredSections!);
        Assert.Contains("appsettings.Development.json", result.Message);
        Assert.Contains("not\ndefaults for production".Replace("\n", " "), result.Message);
    }

    [Fact]
    public async Task StaysQuietAboutASectionTheAppHasAnySettingFrom()
    {
        // Section-level on purpose. An app carrying Jwt__SigningKey plainly has its Jwt section
        // configured; naming the leaves it does not carry (Issuer, Audience) would report keys whose
        // defaults live in a C# options class DeployAI cannot read — noise, and noise is what teaches
        // people to skip the line the one time it matters.
        var result = await Check(
            present: ["Storage__Endpoint", "Jwt__SigningKey", "Tickets__SigningKey"],
            appsettings: BaseAppsettings,
            developmentAppsettings: DevelopmentAppsettings);

        Assert.Empty(result.UnconfiguredSections ?? []);
    }

    [Fact]
    public async Task DoesNotTreatADevelopmentValueAsIfTheKeyWereAlreadyAnswered()
    {
        // The trap in reading the file at all: Jwt__SigningKey has a value there, so counting it as
        // a default would turn the one secret that must be supplied into one that looks handled.
        // Names are taken from that file; values never are.
        var result = await Check(
            present: [],
            appsettings: BaseAppsettings,
            developmentAppsettings: DevelopmentAppsettings);

        Assert.Contains("Jwt", result.UnconfiguredSections!);
        Assert.DoesNotContain("Jwt__SigningKey is set", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task IgnoresSectionsTheFrameworkOwns()
    {
        // Logging and ConnectionStrings appear in every appsettings file ever written. Reporting
        // them would bury the one line that matters.
        var result = await Check(
            present: [],
            appsettings: """{ "Storage": { "Bucket": "b" } }""",
            developmentAppsettings: """{ "Logging": { "LogLevel": {} }, "Kestrel": { "Endpoints": {} } }""");

        Assert.Empty(result.UnconfiguredSections ?? []);
    }

    [Fact]
    public async Task ReportsThatItCouldNotCheck_RatherThanThatNothingIsMissing()
    {
        // The distinction this whole check depends on. "Nothing missing" is safe to deploy on;
        // "I could not compare" is not, and they must never render the same.
        var result = await Check(present: [], listThrows: new InvalidOperationException("401"));

        Assert.True(result.Inconclusive);
        Assert.Empty(result.Missing);
        Assert.Contains("Could not check", result.Message);
        Assert.Contains("will fail once it starts", result.Message);
    }

    [Fact]
    public async Task ReportsThatItCouldNotCheck_WhenTheRepositoryCannotBeRead()
    {
        var result = await Check(present: [], repositoryUnreadable: true);

        Assert.True(result.Inconclusive);
        Assert.Contains("could not read", result.Message);
    }

    private static async Task<RequiredConfigurationResult> Check(
        string[] present,
        string? appsettings = null,
        Exception? listThrows = null,
        bool repositoryUnreadable = false,
        string? developmentAppsettings = null)
    {
        var db = new DeployAIDbContext(new DbContextOptionsBuilder<DeployAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            GitHubId = 1,
            GitHubLogin = "tester",
            GitHubTokenEncrypted = [1],
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "yemenConnect",
            GitHubRepoFullName = $"{Owner}/{Repo}",
            DefaultBranch = Branch,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var target = new DeployTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ProviderName = "coolify",
            ProviderProjectId = "api-uuid",
            ConfigJson = """{"role":"server","serviceDirectory":"backend/src"}""",
            CreatedAt = DateTimeOffset.UtcNow
        };
        project.DeployTargets.Add(target);

        var github = new Mock<IGitHubService>();
        if (!repositoryUnreadable)
        {
            // The nested shape the resolver exists for: backend/src holds project directories.
            github.Setup(g => g.ListAllContentsAsync(
                    It.IsAny<string>(), Owner, Repo, "backend/src", Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new GitHubContentItem("YemenHub.Api", "backend/src/YemenHub.Api", "dir")]);
            github.Setup(g => g.ListAllContentsAsync(
                    It.IsAny<string>(), Owner, Repo, "backend/src/YemenHub.Api", Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new GitHubContentItem("YemenHub.Api.csproj", "backend/src/YemenHub.Api/YemenHub.Api.csproj", "file"),
                    new GitHubContentItem("appsettings.json", "backend/src/YemenHub.Api/appsettings.json", "file")
                ]);
            if (developmentAppsettings is not null)
            {
                github.Setup(g => g.GetFileContentAsync(
                        It.IsAny<string>(), Owner, Repo, "backend/src/YemenHub.Api/appsettings.Development.json",
                        Branch, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(developmentAppsettings);
            }
            github.Setup(g => g.GetFileContentAsync(
                    It.IsAny<string>(), Owner, Repo, "backend/src/YemenHub.Api/YemenHub.Api.csproj", Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync("""<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
            github.Setup(g => g.GetFileContentAsync(
                    It.IsAny<string>(), Owner, Repo, "backend/src/YemenHub.Api/appsettings.json", Branch, It.IsAny<CancellationToken>()))
                .ReturnsAsync(appsettings ?? Appsettings);
        }

        var resolver = new RepositoryLayoutResolver(github.Object);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<byte[]>())).Returns("gh-token");

        var management = new Mock<IProviderManagement>();
        var setup = management.Setup(m => m.ListEnvVarsAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<CancellationToken>()));
        if (listThrows is not null)
        {
            setup.ThrowsAsync(listThrows);
        }
        else
        {
            setup.ReturnsAsync(present
                .Select(k => new ProviderEnvVar(Guid.NewGuid().ToString(), k, "v", ProviderEnvVarTypes.Plain, [], false))
                .ToList());
        }

        var factory = new Mock<IProviderManagementFactory>();
        factory.Setup(f => f.GetManagement(It.IsAny<string>())).Returns(management.Object);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var check = new RequiredConfigurationCheck(
            db, resolver, resolver, new EnvVarDetector(),
            factory.Object, tokens.Object, encryption.Object,
            NullLogger<RequiredConfigurationCheck>.Instance);

        return await check.CheckAsync(project, target, Branch, CancellationToken.None);
    }
}
