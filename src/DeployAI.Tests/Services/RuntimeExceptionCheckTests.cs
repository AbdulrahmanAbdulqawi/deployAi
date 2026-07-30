using DeployAI.Api.Services;
using DeployAI.Core.Providers;
using DeployAI.Data.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DeployAI.Tests.Services;

/// <summary>
/// The check exists because a deploy went green on every signal DeployAI had while the app was
/// broken for everyone using it. Its patterns are therefore tested against output real deployments
/// actually produced — a matcher tuned against invented log text would agree with itself and still
/// miss the thing it was written for.
/// </summary>
public class RuntimeExceptionCheckTests
{
    /// <summary>
    /// Verbatim from yemenConnect's running API on 2026-07-30, including the startup warnings that
    /// a perfectly healthy instance of it logs every time. Those warnings are the reason this is a
    /// fixture and not a guess: matching on the word "Exception", or on any line the app logs at
    /// warning level, flags a working app four times before it flags a broken one.
    /// </summary>
    private const string RealDotnetLog = """
        warn: Microsoft.EntityFrameworkCore.Model.Validation[10620]
              The property 'Member.Interests' is a collection or enumeration type with a value converter but with no value comparer.
        warn: Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository[60]
              Storing keys in a directory '/root/.aspnet/DataProtection-Keys' that may not be persisted outside of the container.
        warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
              No XML encryptor configured. Key {guid} may be persisted to storage in unencrypted form.
        info: Microsoft.Hosting.Lifetime[14]
              Now listening on: http://[::]:8080
        fail: Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware[1]
              An unhandled exception has occurred while executing the request.
              System.InvalidOperationException: The LINQ expression 'DbSet<Member>()
                  .Where(m => m.TenantId == @ef_filter__TenantId && m.DeletedAt == null)
                  .Count(m => (int)m.Status == 0 && !(m.IsDeleted))' could not be translated. Additional information: Translation of member 'IsDeleted' on entity type 'Member' failed.
                 at Microsoft.EntityFrameworkCore.Query.QueryableMethodTranslatingExpressionVisitor.Translate(Expression expression)
                 at Program.<>c.<<<Main>$>b__0_19>d.MoveNext() in /src/YemenHub.Api/Program.cs:line 278
        info: Microsoft.EntityFrameworkCore.Database.Command[20101]
              Executed DbCommand (8ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
              SELECT 1
        """;

    [Fact]
    public void FindsTheFailureInRealOutput_AndNamesTheException()
    {
        var findings = RuntimeExceptionPatterns.Find(RealDotnetLog);

        var only = Assert.Single(findings);
        Assert.StartsWith("System.InvalidOperationException: The LINQ expression", only.Summary);
        Assert.Equal(1, only.Count);
    }

    [Fact]
    public void JoinsAMessageThatWrappedOntoFollowingLines()
    {
        // The first real run of this check reported "...The LINQ expression 'DbSet<Member>()" and
        // stopped there, which names the exception but not the part anyone can act on. EF puts the
        // query on the first line and the reason several lines below it.
        var only = Assert.Single(RuntimeExceptionPatterns.Find(RealDotnetLog));

        Assert.Contains("Translation of member 'IsDeleted'", only.Summary);

        // And stops at the stack, rather than pulling frames into the message.
        Assert.DoesNotContain(" at Microsoft.EntityFrameworkCore", only.Summary);
    }

    [Fact]
    public void StopsAMessageAtTheNextLogEntry()
    {
        var only = Assert.Single(RuntimeExceptionPatterns.Find(
            """
            fail: Some.Category[1]
                  System.Exception: it broke
            info: Microsoft.Hosting.Lifetime[0]
                  Application is shutting down...
            """));

        Assert.Equal("System.Exception: it broke", only.Summary);
    }

    [Fact]
    public void IgnoresTheWarningsAHealthyAppLogsAtStartup()
    {
        // Four warn: lines and two info: lines are in the fixture above. A working yemenConnect
        // logs all of them on every boot, so any of them counting as a failure makes the check
        // fire on every deploy of every app -- and a check that always fires is not read.
        var healthy = string.Join('\n', RealDotnetLog.Split('\n').Where(l => !l.StartsWith("fail:") &&
            !l.Contains("System.InvalidOperationException") && !l.TrimStart().StartsWith("at ") &&
            !l.Contains(".Where(m =>") && !l.Contains(".Count(m =>") && !l.Contains("An unhandled exception")));

        Assert.Empty(RuntimeExceptionPatterns.Find(healthy));
    }

    [Fact]
    public void CountsRepeatsOfTheSameFailureAsOneFinding()
    {
        // An endpoint the landing page calls fails once per visitor. Listing it 200 times buries
        // everything else; saying it happened 200 times is the useful form of the same fact.
        var repeated = string.Join('\n', Enumerable.Repeat(RealDotnetLog, 3));

        var only = Assert.Single(RuntimeExceptionPatterns.Find(repeated));
        Assert.Equal(3, only.Count);
    }

    [Fact]
    public void ReportsCriticalAsWellAsError()
    {
        var findings = RuntimeExceptionPatterns.Find(
            """
            crit: Microsoft.AspNetCore.Hosting.Diagnostics[6]
                  Application startup exception
                  System.InvalidOperationException: Jwt configuration missing.
            """);

        Assert.Single(findings);
        Assert.Contains("Jwt configuration missing", findings[0].Summary);
    }

    [Fact]
    public void FindsAPythonTraceback()
    {
        // The type line comes last in a traceback, after the frames -- the opposite order to .NET,
        // which is why the summary scans forward for it rather than reading the next line.
        var findings = RuntimeExceptionPatterns.Find(
            """
            INFO:     127.0.0.1:0 - "GET /health HTTP/1.1" 200 OK
            Traceback (most recent call last):
              File "/app/main.py", line 42, in stats
                return count()
              File "/app/db.py", line 7, in count
                raise ValueError("no such column: is_deleted")
            ValueError: no such column: is_deleted
            """);

        var only = Assert.Single(findings);
        Assert.Equal("ValueError: no such column: is_deleted", only.Summary);
    }

    [Fact]
    public void FindsABareNodeStack()
    {
        var findings = RuntimeExceptionPatterns.Find(
            """
            Listening on port 3000
            TypeError: Cannot read properties of undefined (reading 'id')
                at handler (/app/routes/stats.js:12:24)
                at Layer.handle (/app/node_modules/express/lib/router/layer.js:95:5)
            """);

        var only = Assert.Single(findings);
        Assert.Equal("TypeError: Cannot read properties of undefined (reading 'id')", only.Summary);
    }

    [Fact]
    public void DoesNotTreatProseAsAStack()
    {
        // "Error: ..." appears in ordinary output all the time. Requiring a stack frame under it is
        // what separates a crash from a log line about one.
        Assert.Empty(RuntimeExceptionPatterns.Find(
            """
            Error: retrying connection to redis in 5s
            Connected.
            """));
    }

    [Fact]
    public void FindsAGoPanic()
    {
        var findings = RuntimeExceptionPatterns.Find(
            """
            panic: runtime error: invalid memory address or nil pointer dereference
            goroutine 1 [running]:
            """);

        Assert.Single(findings);
        Assert.Contains("invalid memory address", findings[0].Summary);
    }

    [Fact]
    public async Task ReportsThatItCouldNotLook_WhenTheContainerCannotBeRead()
    {
        // The distinction the whole check depends on. A stopped container is the state most worth
        // hearing about, and it is exactly the state that returns no log — so "no errors found"
        // must never be the answer to it.
        var scan = await Scan(throws: new InvalidOperationException("Application is not running."));

        Assert.True(scan.Inconclusive);
        Assert.Empty(scan.Findings);
        Assert.Contains("not running", scan.Reason);
    }

    [Fact]
    public async Task ReportsThatItCouldNotLook_WhenTheProviderHasNoLogs()
    {
        var scan = await Scan(supported: false);

        Assert.True(scan.Inconclusive);
    }

    [Fact]
    public async Task ReportsACleanLogAsConclusive()
    {
        var scan = await Scan(logs: "info: Microsoft.Hosting.Lifetime[14]\n      Now listening on: http://[::]:8080");

        Assert.False(scan.Inconclusive);
        Assert.Empty(scan.Findings);
    }

    private static async Task<RuntimeExceptionScan> Scan(
        string? logs = null,
        Exception? throws = null,
        bool supported = true)
    {
        var runtimeLogs = new Mock<IProviderRuntimeLogs>();
        var setup = runtimeLogs.Setup(r => r.GetRuntimeLogsAsync(
            It.IsAny<ProviderCredentials>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()));
        if (throws is not null)
        {
            setup.ThrowsAsync(throws);
        }
        else
        {
            setup.ReturnsAsync(logs ?? string.Empty);
        }

        var factory = new Mock<IProviderRuntimeLogsFactory>();
        factory.Setup(f => f.GetRuntimeLogs(It.IsAny<string>()))
            .Returns(supported ? runtimeLogs.Object : null);

        var tokens = new Mock<IProviderCredentialTokenService>();
        tokens.Setup(t => t.GetTokenAsync(It.IsAny<ProviderCredential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");

        var check = new RuntimeExceptionCheck(
            factory.Object, tokens.Object, NullLogger<RuntimeExceptionCheck>.Instance);

        return await check.ScanAsync(
            new DeployTarget
            {
                Id = Guid.NewGuid(),
                ProviderName = ProviderNameValues.Coolify,
                ProviderProjectId = "app-uuid",
                ConfigJson = """{"role":"server"}""",
                CreatedAt = DateTimeOffset.UtcNow
            },
            200,
            CancellationToken.None);
    }
}
