using System.Text;

namespace DeployAI.Core.Domains;

/// <summary>
/// Names an app under a zone DeployAI controls, so it can be given a working HTTPS address without
/// the user configuring anything.
/// </summary>
/// <remarks>
/// The alternative today is the server's generated <c>{uuid}.{ip}.sslip.io</c> address, which is
/// served over plain HTTP, is impossible to read out loud, and changes if the app is recreated.
/// </remarks>
public static class PlatformSubdomain
{
    /// <summary>
    /// Builds <c>{slug}.{platformDomain}</c> from a project name, or null when no platform zone is
    /// configured — an offer that cannot be honoured must not be made.
    /// </summary>
    /// <param name="taken">
    /// Names already in use, so a second project called "shop" becomes "shop-2" rather than
    /// silently colliding with the first.
    /// </param>
    public static string? TryBuild(
        string projectName,
        string? platformDomain,
        IReadOnlySet<string>? taken = null)
    {
        if (string.IsNullOrWhiteSpace(platformDomain))
        {
            return null;
        }

        var zone = platformDomain.Trim().Trim('.').ToLowerInvariant();
        var slug = Slugify(projectName);
        if (slug.Length == 0)
        {
            return null;
        }

        var candidate = $"{slug}.{zone}";
        if (taken is null || !taken.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            candidate = $"{slug}-{suffix}.{zone}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Reduces a project name to a DNS label: lowercase, alphanumerics and single hyphens, never
    /// starting or ending with one, and no longer than a label may be.
    /// </summary>
    public static string Slugify(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(projectName.Length);
        foreach (var character in projectName.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 63 ? slug[..63].Trim('-') : slug;
    }
}
