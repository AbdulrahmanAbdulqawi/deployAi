using DeployAI.Api.Services;

namespace DeployAI.Tests.Services;

public class SplitOriginClientWiringAnalyzerTests
{
    [Fact]
    public void AnalyzeBundleScripts_TicketHubLikeBundle_IsNotWired()
    {
        var bundle = """
            this.apiUrl="/api/v1/auth";
            this.apiUrl="/api/Settings";
            this.http.post(`${this.apiUrl}/login`, e);
            """;

        var analysis = SplitOriginClientWiringAnalyzer.AnalyzeBundleScripts([bundle]);

        Assert.True(analysis.HasRelativeApiPaths);
        Assert.False(analysis.HasWithInterceptors);
        Assert.False(analysis.IsWired);
    }

    [Fact]
    public void AnalyzeBundleScripts_IdaaraLikeBundle_IsWired()
    {
        var bundle = """
            withInterceptors([apiBaseInterceptor]);
            apiBaseUrl:"https://idaara-api-production.up.railway.app";
            this.apiUrl="/api/v1/auth";
            """;

        var analysis = SplitOriginClientWiringAnalyzer.AnalyzeBundleScripts([bundle]);

        Assert.True(analysis.HasWithInterceptors);
        Assert.True(analysis.HasNonEmptyApiBaseUrl);
        Assert.True(analysis.IsWired);
    }

    [Fact]
    public void HasAuthRouteMismatch_ReelHubPattern_ReturnsTrue()
    {
        const string authService = """private apiUrl = '/api/Auth';""";
        const string authController = """[Route("api/v1/auth")]""";

        Assert.True(SplitOriginClientWiringAnalyzer.HasAuthRouteMismatch(authService, authController));
    }

    [Fact]
    public void HasAuthRouteMismatch_TicketHubPattern_ReturnsFalse()
    {
        const string authService = """private apiUrl = '/api/v1/auth';""";
        const string authController = """[Route("api/v1/auth")]""";

        Assert.False(SplitOriginClientWiringAnalyzer.HasAuthRouteMismatch(authService, authController));
    }
}
