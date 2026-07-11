namespace DeployAI.Core.Providers;

public enum ProviderName
{
    Vercel,
    Railway,
    Coolify
}

public static class ProviderNameValues
{
    public const string Vercel = "vercel";
    public const string Railway = "railway";
    public const string Coolify = "coolify";

    public static string ToApiValue(ProviderName provider) => provider switch
    {
        ProviderName.Vercel => Vercel,
        ProviderName.Railway => Railway,
        ProviderName.Coolify => Coolify,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };

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
