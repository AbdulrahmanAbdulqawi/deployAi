namespace DeployAI.Core.Domains;

/// <summary>
/// The error codes a DNS or registrar provider raises that mean something other than "the caller
/// got it wrong".
/// </summary>
/// <remarks>
/// Shared constants rather than per-provider strings because the API edge has to recognise them to
/// answer 429 and 503, and a provider inventing its own spelling gets a 400 instead — which tells
/// the caller their input was bad when nothing was even checked, and invites the immediate retry
/// that makes a rate-limit window longer. Any new provider reuses these two.
/// </remarks>
public static class DnsErrorCodes
{
    /// <summary>The provider is throttling. Says nothing about the credential. Answered as 429.</summary>
    public const string RateLimited = "dns_provider_rate_limited";

    /// <summary>The provider could not be reached at all. Answered as 503.</summary>
    public const string Unreachable = "dns_provider_unreachable";
}
