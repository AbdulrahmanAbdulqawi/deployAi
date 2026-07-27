using System.Text.RegularExpressions;

namespace DeployAI.Api.Services;

/// <summary>The outcome of a single live HTTP probe against a deployed endpoint.</summary>
internal enum ProbeCheckStatus
{
    Passed,
    Failed,
    Warning,
    Skipped
}

/// <summary>A probe's result: status, human-readable message, and (if failed) a suggested remediation action code.</summary>
internal sealed record ProbeCheckResult(
    ProbeCheckStatus Status,
    string Message,
    string? SuggestedAction = null);

/// <summary>
/// Low-level HTTP probes against a deployment's live URLs - homepage reachability, SPA shell
/// markers, split-origin bundle wiring, CORS preflight, and API health - the building blocks
/// <see cref="DeploymentVerificationService"/> composes into full verification checks.
/// </summary>
internal static class DeploymentEndpointProbes
{
    internal static async Task<ProbeCheckResult> CheckReachableAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Request failed ({(int)response.StatusCode}): {url}",
                    SuggestedAction: "redeploy_server");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, $"Reachable: {url}");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"Request error: {ex.Message}",
                SuggestedAction: "redeploy_server");
        }
    }

    internal static async Task<ProbeCheckResult> CheckWebsiteHomepageAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        var url = $"{websiteUrl.TrimEnd('/')}/";
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                body.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Website returned 404. The deployment may be missing index.html — check vercel.json outputDirectory (Angular apps often need a /browser suffix).",
                    SuggestedAction: "fix_output_directory");
            }

            if ((int)response.StatusCode >= 500)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Website returned {(int)response.StatusCode}: {url}",
                    SuggestedAction: "redeploy_website");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Warning,
                    $"Website returned {(int)response.StatusCode}: {url}",
                    SuggestedAction: "redeploy_website");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, $"Website homepage loaded: {url}");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"Website request error: {ex.Message}",
                SuggestedAction: "redeploy_website");
        }
    }

    internal static ProbeCheckResult CheckSpaShell(string html)
    {
        var hasAppRoot = html.Contains("<app-root", StringComparison.OrdinalIgnoreCase);
        var hasScripts = html.Contains("<script", StringComparison.OrdinalIgnoreCase);

        if (!hasAppRoot && !hasScripts)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Warning,
                "Homepage HTML does not look like an SPA shell (no app-root or script tags).",
                SuggestedAction: "fix_output_directory");
        }

        return new ProbeCheckResult(ProbeCheckStatus.Passed, "SPA shell markers found in homepage HTML.");
    }

    internal static async Task<ProbeCheckResult> CheckSplitOriginApiHealthAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Health check failed ({(int)response.StatusCode}): {url}",
                    SuggestedAction: "redeploy_server");
            }

            if (!body.Contains("healthy", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Health check returned an unexpected body: {url}",
                    SuggestedAction: "redeploy_server");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, $"Health check passed: {url}");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"Health check error: {ex.Message}",
                SuggestedAction: "redeploy_server");
        }
    }

    internal static async Task<ProbeCheckResult> CheckProxiedApiHealthAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

            if (!response.IsSuccessStatusCode)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Proxy health check failed ({(int)response.StatusCode}): {url}",
                    SuggestedAction: "reconnect");
            }

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    "Proxy health check returned HTML instead of API JSON. Verify Vercel rewrites to the Railway API.",
                    SuggestedAction: "reconnect");
            }

            if (!body.Contains("\"status\"", StringComparison.OrdinalIgnoreCase) &&
                !body.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Proxy health check returned an unexpected body: {url}",
                    SuggestedAction: "reconnect");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, $"Proxy health check passed: {url}");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"Proxy health check error: {ex.Message}",
                SuggestedAction: "reconnect");
        }
    }

    internal static async Task<ProbeCheckResult> CheckProxiedApiLoginAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var proxyWorking = await EvaluateProxiedApiPostResponseAsync(client, websiteUrl, cancellationToken);

            if (proxyWorking == false)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    "Website proxy returned 405 for POST /api/v1/auth/login. Production domain may still point at an old Vercel deployment.",
                    SuggestedAction: "reconnect");
            }

            if (proxyWorking is null)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    "Could not reach POST /api/v1/auth/login through the website proxy.",
                    SuggestedAction: "reconnect");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, "Website proxy check passed: POST /api/v1/auth/login");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"Website proxy check error: {ex.Message}",
                SuggestedAction: "reconnect");
        }
    }

    internal static async Task<ProbeCheckResult> CheckCorsHeaderAsync(
        HttpClient client,
        string apiUrl,
        string origin,
        CancellationToken cancellationToken)
    {
        var preflightUrl = apiUrl.TrimEnd('/') + "/api/v1/auth/login";
        using var request = new HttpRequestMessage(HttpMethod.Options, preflightUrl);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"CORS preflight failed ({(int)response.StatusCode}): {preflightUrl}",
                    SuggestedAction: "reconnect");
            }

            if (!response.Headers.Contains("Access-Control-Allow-Origin"))
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Failed,
                    $"Server reachable but Access-Control-Allow-Origin is missing for {origin}.",
                    SuggestedAction: "reconnect");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, $"CORS check passed for origin {origin}.");
        }
        catch (Exception ex)
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Failed,
                $"CORS check error: {ex.Message}",
                SuggestedAction: "reconnect");
        }
    }

    internal static async Task<ProbeCheckResult?> CheckSplitOriginSpaWiringAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = websiteUrl.TrimEnd('/');
            var html = await client.GetStringAsync($"{baseUrl}/", cancellationToken);
            var scriptBodies = new List<string> { html };
            foreach (var source in ExtractScriptSources(html))
            {
                var scriptUrl = source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? source
                    : $"{baseUrl}/{source.TrimStart('/')}";

                try
                {
                    scriptBodies.Add(await client.GetStringAsync(scriptUrl, cancellationToken));
                }
                catch
                {
                    // Best-effort: continue with other bundles.
                }
            }

            if (scriptBodies.Count == 1)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Warning,
                    "Could not inspect deployed JavaScript bundles for split-origin wiring.",
                    SuggestedAction: "reconnect");
            }

            var analysis = SplitOriginClientWiringAnalyzer.AnalyzeBundleScripts(scriptBodies);
            if (!analysis.IsWired)
            {
                return new ProbeCheckResult(
                    ProbeCheckStatus.Warning,
                    "Deployed SPA bundle is missing split-origin wiring (apiBaseInterceptor and apiBaseUrl). A production redeploy may be required.",
                    SuggestedAction: "reconnect");
            }

            return new ProbeCheckResult(ProbeCheckStatus.Passed, "Split-origin SPA bundle wiring detected.");
        }
        catch
        {
            return new ProbeCheckResult(
                ProbeCheckStatus.Warning,
                "Could not inspect deployed SPA for split-origin wiring.",
                SuggestedAction: "reconnect");
        }
    }

    internal static async Task AppendReachableMessageAsync(
        HttpClient client,
        string url,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var result = await CheckReachableAsync(client, url, cancellationToken);
        messages.Add(FormatLegacyMessage(label, result));
    }

    internal static async Task AppendSplitOriginHealthMessageAsync(
        HttpClient client,
        string url,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var result = await CheckSplitOriginApiHealthAsync(client, url, cancellationToken);
        messages.Add(FormatLegacyMessage(label, result));
    }

    internal static async Task AppendProxiedHealthMessageAsync(
        HttpClient client,
        string url,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var result = await CheckProxiedApiHealthAsync(client, url, cancellationToken);
        messages.Add(FormatLegacyMessage(label, result));
    }

    internal static async Task AppendProxiedLoginMessageAsync(
        HttpClient client,
        string websiteUrl,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var result = await CheckProxiedApiLoginAsync(client, websiteUrl, cancellationToken);
        messages.Add(FormatLegacyMessage(label, result));
    }

    internal static async Task AppendCorsMessageAsync(
        HttpClient client,
        string apiUrl,
        string origin,
        string label,
        List<string> messages,
        CancellationToken cancellationToken)
    {
        var result = await CheckCorsHeaderAsync(client, apiUrl, origin, cancellationToken);
        messages.Add(FormatLegacyMessage(label, result));
    }

    internal static async Task<bool?> ProbeDeployedSpaWiredToApiAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        var result = await CheckSplitOriginSpaWiringAsync(client, websiteUrl, cancellationToken);
        if (result is null)
        {
            return null;
        }

        return result.Status switch
        {
            ProbeCheckStatus.Passed => true,
            ProbeCheckStatus.Warning when result.Message.Contains("missing split-origin", StringComparison.OrdinalIgnoreCase) => false,
            _ => null
        };
    }

    private static string FormatLegacyMessage(string label, ProbeCheckResult result) =>
        result.Status switch
        {
            ProbeCheckStatus.Passed => $"{label} check passed: {result.Message}",
            ProbeCheckStatus.Warning => $"{label} check warning: {result.Message}",
            _ => $"{label} check failed: {result.Message}"
        };

    private static async Task<bool?> EvaluateProxiedApiPostResponseAsync(
        HttpClient client,
        string websiteUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{websiteUrl.TrimEnd('/')}/api/v1/auth/login");
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";

        if (response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed)
        {
            return false;
        }

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    internal static IReadOnlyList<string> ExtractScriptSources(string html)
    {
        var sources = new List<string>();

        foreach (Match match in Regex.Matches(
                     html,
                     "<script[^>]+src\\s*=\\s*[\"']([^\"']+)[\"']",
                     RegexOptions.IgnoreCase))
        {
            sources.Add(match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(
                     html,
                     "<link[^>]+>",
                     RegexOptions.IgnoreCase))
        {
            if (!match.Value.Contains("modulepreload", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = Regex.Match(
                match.Value,
                "href\\s*=\\s*[\"']([^\"']+)[\"']",
                RegexOptions.IgnoreCase);
            if (href.Success)
            {
                sources.Add(href.Groups[1].Value);
            }
        }

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
