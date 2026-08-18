using System.Globalization;

namespace DeployAI.Core.Domains;

/// <summary>Why a hostname cannot be used, phrased for someone who has never configured DNS.</summary>
public sealed record DomainNameRejection(string Reason);

/// <summary>
/// Turning what a user typed into the one spelling every other component uses.
/// </summary>
/// <remarks>
/// Normalising in one place matters more than it looks. A hostname reaches a resolver, a provider
/// API, a certificate's name list, and a database uniqueness index, and those four only agree if
/// the same transformation ran before each of them. A unicode domain compared against its own
/// punycode form silently mismatches at every one of those boundaries.
/// </remarks>
public static class DomainNameRules
{
    /// <summary>
    /// Normalises <paramref name="typed"/> to an ASCII hostname, or explains why it cannot be one.
    /// </summary>
    public static bool TryNormalize(
        string? typed,
        out string hostname,
        out DomainNameRejection? rejection)
    {
        hostname = string.Empty;
        rejection = null;

        if (string.IsNullOrWhiteSpace(typed))
        {
            rejection = new DomainNameRejection("Enter a domain name.");
            return false;
        }

        var value = typed.Trim();

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            value = value[(scheme + 3)..];
        }

        // Drop anything after the host: a pasted URL brings a path, a query, or a port with it.
        value = value.Split('/', '?', '#')[0].Split(':')[0].Trim().TrimEnd('.');

        if (value.Length == 0)
        {
            rejection = new DomainNameRejection("Enter a domain name.");
            return false;
        }

        // A wildcard cannot be validated by the HTTP-01 challenge Coolify uses, so accepting one
        // here would mean accepting a domain whose certificate can never issue.
        if (value.Contains('*'))
        {
            rejection = new DomainNameRejection(
                "Wildcard domains need a different kind of certificate check than this server does. " +
                "Add each subdomain you want to use instead.");
            return false;
        }

        // Checked before the punycode conversion as well as after: the conversion throws its own
        // generic error on an over-long name, which would replace "too long" with "not valid".
        if (value.Length > 253)
        {
            rejection = new DomainNameRejection("That domain name is too long to be valid.");
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { AllowUnassigned = false, UseStd3AsciiRules = true }
                .GetAscii(value)
                .ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            rejection = new DomainNameRejection($"\"{typed.Trim()}\" is not a valid domain name.");
            return false;
        }

        if (!ascii.Contains('.'))
        {
            rejection = new DomainNameRejection(
                $"\"{ascii}\" is missing a domain ending — it needs something like .com after it.");
            return false;
        }

        if (ascii.Length > 253)
        {
            rejection = new DomainNameRejection("That domain name is too long to be valid.");
            return false;
        }

        if (IsSslip(ascii))
        {
            rejection = new DomainNameRejection(
                "That is the temporary address the server generates. Enter a domain you own instead.");
            return false;
        }

        hostname = ascii;
        return true;
    }

    /// <summary>
    /// Coolify's own stand-in hostname, which encodes the server IP and needs no DNS. Recognised so
    /// it is never treated as a domain the user chose, and never counted as one that has to verify.
    /// </summary>
    public static bool IsSslip(string hostname) =>
        hostname.EndsWith(".sslip.io", StringComparison.OrdinalIgnoreCase) ||
        hostname.EndsWith(".nip.io", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name and record type a user has to create, for the case where DeployAI cannot write it.
    /// Always an A record: the proxy routes on the address, and a CNAME will not do.
    /// </summary>
    public static string RecordNameWithin(string hostname, string zone) =>
        string.Equals(hostname, zone, StringComparison.OrdinalIgnoreCase)
            ? "@"
            : hostname.EndsWith($".{zone}", StringComparison.OrdinalIgnoreCase)
                ? hostname[..^(zone.Length + 1)]
                : hostname;
}
