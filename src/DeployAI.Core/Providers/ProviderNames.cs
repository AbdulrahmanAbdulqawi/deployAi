namespace DeployAI.Core.Providers;

/// <summary>The set of hosting providers DeployAI supports.</summary>
public enum ProviderName
{
    Vercel,
    Railway,
    Coolify
}

/// <summary>
/// String constants and conversions for provider names, matching the values stored as
/// <c>ProviderName</c> on <c>DeployTarget</c>/<c>ProviderCredential</c> records. Most code compares
/// against these constants directly rather than parsing to the <see cref="ProviderName"/> enum.
/// </summary>
public static class ProviderNameValues
{
    public const string Vercel = "vercel";
    public const string Railway = "railway";
    public const string Coolify = "coolify";

    /// <summary>Converts a <see cref="ProviderName"/> enum value to its stored string constant.</summary>
    public static string ToApiValue(ProviderName provider) => provider switch
    {
        ProviderName.Vercel => Vercel,
        ProviderName.Railway => Railway,
        ProviderName.Coolify => Coolify,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };

    /// <summary>Parses a stored provider name string back into the <see cref="ProviderName"/> enum, case-insensitively.</summary>
    public static bool TryParse(string? value, out ProviderName provider)
    {
        if (string.Equals(value, Vercel, StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderName.Vercel;
            return true;
        }

        if (string.Equals(value, Railway, StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderName.Railway;
            return true;
        }

        if (string.Equals(value, Coolify, StringComparison.OrdinalIgnoreCase))
        {
            provider = ProviderName.Coolify;
            return true;
        }

        provider = default;
        return false;
    }
}
