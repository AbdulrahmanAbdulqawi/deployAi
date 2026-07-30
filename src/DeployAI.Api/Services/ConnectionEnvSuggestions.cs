namespace DeployAI.Api.Services;

/// <summary>A value DeployAI can offer for a setting, and where it came from.</summary>
/// <param name="Value">What to prefill.</param>
/// <param name="Source">Shown to the user, so a prefilled box is never an unexplained value.</param>
/// <param name="Exposure">
/// Set when accepting the suggestion grants the app more than it needs. Null for values that carry
/// no privilege, like a URL.
/// </param>
public sealed record EnvSuggestion(string Value, string Source, string? Exposure = null);

/// <summary>
/// Settings an app asks for that DeployAI already holds, because the user linked the connection they
/// describe.
/// </summary>
/// <remarks>
/// The standing rule is that no credential should have to be typed or pasted. Some genuinely cannot
/// be helped — a GitHub App's private key is shown once by GitHub and never again — but a value the
/// user already gave DeployAI is not one of them. Mirqab asks for a Coolify URL and a Coolify API
/// token, and DeployAI is holding both, in the connection that is about to deploy it.
/// <para>
/// Matched by suffix rather than by exact name, the way <c>FillStorageSuggestionsAsync</c> maps a
/// storage account onto <c>S3_*</c>, because apps prefix these with their own name
/// (<c>MIRQAB_COOLIFY_URL</c>).
/// </para>
/// </remarks>
public static class ConnectionEnvSuggestions
{
    /// <summary>
    /// What a linked Coolify connection can answer. The token is offered rather than assumed: it is
    /// DeployAI's own, and it can manage every application on that server — so it travels with the
    /// exposure attached, and the user can clear the box.
    /// </summary>
    public static IReadOnlyDictionary<string, EnvSuggestion> ForCoolify(
        string? instanceUrl,
        string? apiToken,
        IEnumerable<string> variableNames)
    {
        var suggestions = new Dictionary<string, EnvSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in variableNames)
        {
            var upper = name.ToUpperInvariant();

            if (IsCoolifyUrl(upper) && !string.IsNullOrWhiteSpace(instanceUrl))
            {
                suggestions[name] = new EnvSuggestion(instanceUrl.Trim(), "your Coolify connection");
            }
            else if (IsCoolifyToken(upper) && !string.IsNullOrWhiteSpace(apiToken))
            {
                suggestions[name] = new EnvSuggestion(
                    apiToken.Trim(),
                    "your Coolify connection",
                    "This is the token DeployAI itself uses. It can manage every application on that "
                    + "Coolify server, not just this one. Clear it and paste a narrower token if you "
                    + "would rather this app could not.");
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Values a Coolify GitHub App record can answer. The app id is public; the private key is not
    /// here, because Coolify is not asked for it and GitHub cannot reissue it.
    /// </summary>
    public static IReadOnlyDictionary<string, EnvSuggestion> ForGitHubApp(
        string? appId,
        IEnumerable<string> variableNames)
    {
        var suggestions = new Dictionary<string, EnvSuggestion>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(appId))
        {
            return suggestions;
        }

        foreach (var name in variableNames)
        {
            var upper = name.ToUpperInvariant();
            if (upper.EndsWith("GITHUB_APP_ID", StringComparison.Ordinal) ||
                upper.EndsWith("GITHUBAPPID", StringComparison.Ordinal))
            {
                suggestions[name] = new EnvSuggestion(appId.Trim(), "the Coolify GitHub App you selected");
            }
        }

        return suggestions;
    }

    private static bool IsCoolifyUrl(string upper) =>
        upper.EndsWith("COOLIFY_URL", StringComparison.Ordinal) ||
        upper.EndsWith("COOLIFY_BASE_URL", StringComparison.Ordinal) ||
        upper.EndsWith("COOLIFY_INSTANCE_URL", StringComparison.Ordinal);

    /// <summary>
    /// Deliberately not matched on "TOKEN" alone. Every app has tokens that have nothing to do with
    /// Coolify, and prefilling one of those with a Coolify API token would hand it out for no reason
    /// and look authoritative doing it.
    /// </summary>
    private static bool IsCoolifyToken(string upper) =>
        upper.EndsWith("COOLIFY_TOKEN", StringComparison.Ordinal) ||
        upper.EndsWith("COOLIFY_API_TOKEN", StringComparison.Ordinal) ||
        upper.EndsWith("COOLIFY_API_KEY", StringComparison.Ordinal);
}
