using System.Text.Json;

namespace DeployAI.Api.Services;

/// <summary>
/// Credentials a user saved once, on their account, so every app that needs one is filled in rather
/// than asked again.
/// </summary>
/// <remarks>
/// Scoped to the user rather than to a project on purpose: the whole point is that the second app
/// needing a GitHub App private key does not have to be told about it. Keyed by a canonical name
/// (<c>GITHUB_APP_PRIVATE_KEY</c>) rather than by whatever an app happens to call its variable,
/// because <c>MIRQAB_GITHUB_PRIVATE_KEY</c> and <c>YEMENHUB_GITHUB_PRIVATE_KEY</c> want the same
/// value and matching is done by suffix at suggestion time.
/// </remarks>
public sealed class AccountSecretsStore
{
    /// <summary>The GitHub App this user's deployments authenticate as.</summary>
    public const string GitHubAppId = "GITHUB_APP_ID";

    /// <summary>Its private key (PEM). GitHub shows this once and will not reissue it.</summary>
    public const string GitHubAppPrivateKey = "GITHUB_APP_PRIVATE_KEY";

    /// <summary>Everything this store knows how to hold, so the UI and the matcher agree.</summary>
    public static IReadOnlyList<string> KnownNames { get; } = [GitHubAppId, GitHubAppPrivateKey];

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static AccountSecretsStore Parse(string? json)
    {
        var store = new AccountSecretsStore();
        if (string.IsNullOrWhiteSpace(json))
        {
            return store;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (values is not null)
            {
                foreach (var (key, value) in values)
                {
                    store._values[key] = value;
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable blob — an empty store beats failing every request that touches settings.
        }

        return store;
    }

    public string Serialize() => JsonSerializer.Serialize(_values);

    /// <summary>Saves a value, or forgets it when the user clears the field.</summary>
    public void Set(string name, string? value)
    {
        var key = name.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _values.Remove(key);
            return;
        }

        _values[key] = value.Trim();
    }

    public string? Find(string name) =>
        _values.TryGetValue(name.Trim(), out var value) ? value : null;

    /// <summary>Which names hold a value — for a listing that must never return the values.</summary>
    public IReadOnlySet<string> NamesSet() =>
        _values.Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a repository's own variable names should be filled with, matched by suffix so an app's
    /// prefix does not have to be known in advance.
    /// </summary>
    public IReadOnlyDictionary<string, EnvSuggestion> SuggestFor(IEnumerable<string> variableNames)
    {
        var suggestions = new Dictionary<string, EnvSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in variableNames)
        {
            var upper = name.ToUpperInvariant();

            var saved = IsPrivateKey(upper) ? Find(GitHubAppPrivateKey)
                : IsAppId(upper) ? Find(GitHubAppId)
                : null;

            if (!string.IsNullOrWhiteSpace(saved))
            {
                suggestions[name] = new EnvSuggestion(saved, "the GitHub App saved on your account");
            }
        }

        return suggestions;
    }

    private static bool IsPrivateKey(string upper) =>
        upper.EndsWith("GITHUB_PRIVATE_KEY", StringComparison.Ordinal) ||
        upper.EndsWith("GITHUB_APP_PRIVATE_KEY", StringComparison.Ordinal) ||
        upper.EndsWith("GITHUBAPPPRIVATEKEY", StringComparison.Ordinal);

    private static bool IsAppId(string upper) =>
        upper.EndsWith("GITHUB_APP_ID", StringComparison.Ordinal) ||
        upper.EndsWith("GITHUBAPPID", StringComparison.Ordinal);
}
