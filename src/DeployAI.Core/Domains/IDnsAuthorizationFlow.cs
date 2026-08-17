using DeployAI.Core.Providers;

namespace DeployAI.Core.Domains;

/// <summary>Where an approval request has got to.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum DnsAuthorizationState
{
    /// <summary>Waiting for the account holder to approve it in their browser.</summary>
    Pending = 0,

    /// <summary>Approved; credentials were returned.</summary>
    Approved = 1,

    /// <summary>The account holder said no.</summary>
    Denied = 2,

    /// <summary>The window closed before anyone approved it.</summary>
    Expired = 3,

    /// <summary>The provider could not be asked. Says nothing about the request.</summary>
    Unreachable = 4
}

/// <summary>An approval request the user has to complete in their browser.</summary>
/// <param name="Verifier">
/// The PKCE verifier. Held by DeployAI and never shown to the user or sent to the browser — it is
/// what proves the poll comes from whoever started the request.
/// </param>
public sealed record DnsAuthorizationRequest(
    string RequestToken,
    string ApprovalUrl,
    string Verifier,
    DateTimeOffset ExpiresAt);

/// <summary>The result of asking whether an approval request has been completed.</summary>
public sealed record DnsAuthorizationResult(
    DnsAuthorizationState State,
    string Message,
    ProviderCredentials? Credentials = null);

/// <summary>
/// A provider that can hand over credentials through an approval flow, so the user never creates,
/// copies or pastes a key.
/// </summary>
/// <remarks>
/// Optional: a provider without one is connected by pasting its fields instead. Where it exists it
/// is strictly better — nothing to scope wrong, nothing to truncate, and no secret through a
/// clipboard, which between them account for every failed connection attempt in this feature's
/// short history.
/// </remarks>
public interface IDnsAuthorizationFlow
{
    string ProviderName { get; }

    /// <summary>Starts a request and returns the URL the account holder must visit.</summary>
    Task<DnsAuthorizationRequest> BeginAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asks whether the request has been approved yet.
    /// </summary>
    /// <remarks>
    /// The secret is returned exactly once, on the first successful poll, and never again — so a
    /// caller that receives <see cref="DnsAuthorizationState.Approved"/> must persist it before
    /// doing anything else that could fail.
    /// </remarks>
    Task<DnsAuthorizationResult> PollAsync(
        DnsAuthorizationRequest request, CancellationToken cancellationToken);
}
