using System.Text.Json.Serialization;

namespace DeployAI.Data.Entities;

/// <summary>Where a domain came from, which decides who is responsible for its DNS.</summary>
/// <remarks>
/// Serialized by name because the client switches on the name. The API now configures enum-as-string
/// globally (see <c>Program.cs</c>), so this attribute is no longer what carries the guarantee over
/// the wire — it is kept because it also covers direct <c>JsonSerializer.Serialize</c> calls that
/// never pass through MVC, such as <see cref="ProjectDomain.ObservationsJson"/>. Before the global
/// convention existed, a missing attribute meant the value crossed as an integer, every client
/// comparison missed, and the UI rendered its default branch — which is how a domain waiting on DNS
/// came out labelled "Removed".
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainSource
{
    /// <summary>The user owns it and manages its DNS somewhere DeployAI cannot reach.</summary>
    UserProvided = 0,

    /// <summary>A subdomain of a zone DeployAI controls, needing no DNS work from the user at all.</summary>
    PlatformSubdomain = 1,

    /// <summary>The user owns it and has connected the DNS provider, so DeployAI writes the record.</summary>
    ManagedZone = 2,

    /// <summary>
    /// Registered through DeployAI. Reserved rather than implemented — buying a domain means money,
    /// a registration agreement accepted on someone's behalf, and a renewal DeployAI would then owe
    /// them. Recorded as a gap; an unused enum member is a note, whereas an unused interface would
    /// be a promise.
    /// </summary>
    Registrar = 3
}

/// <summary>
/// How far a domain has got towards serving traffic on a certificate a browser will accept.
/// </summary>
/// <remarks>
/// The four terminal states are two pairs, and the split within each pair is the point. A check
/// that failed and a check that could not run lead to opposite actions — one is the user's DNS to
/// fix, the other is ours to retry — and a single "failed" state would tell a user to go change a
/// record that was never wrong.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DomainStatus
{
    /// <summary>Recorded, nothing attempted yet.</summary>
    Pending = 0,

    /// <summary>Waiting for the record to appear, whether the user is creating it or DeployAI wrote it.</summary>
    DnsPending = 1,

    /// <summary>A public resolver confirms the name reaches this server.</summary>
    DnsVerified = 2,

    /// <summary>The provider holds the domain and a read-back confirmed it; routing needs a deploy.</summary>
    Assigned = 3,

    /// <summary>Routing deploy done, the certificate authority has not answered yet.</summary>
    CertificatePending = 4,

    /// <summary>Serving a valid certificate covering this name.</summary>
    Active = 5,

    /// <summary>DNS was checked and is wrong. The user has something to fix.</summary>
    DnsFailed = 6,

    /// <summary>DNS could never be checked. Says nothing about the domain, and blames nobody.</summary>
    DnsUnverifiable = 7,

    /// <summary>The certificate was attempted and did not issue.</summary>
    CertificateFailed = 8,

    /// <summary>The host could never be reached over TLS, so issuance was never observed either way.</summary>
    CertificateUnverifiable = 9,

    /// <summary>The provider refuses the name because another resource already holds it.</summary>
    Conflicted = 10,

    /// <summary>Removed by the user; any record DeployAI wrote has been released.</summary>
    Retired = 11
}

/// <summary>
/// A domain a project should be reachable at, and everything learned while trying to make that
/// true.
/// </summary>
/// <remarks>
/// Kept as its own row rather than another field on the deploy target's config blob because it has
/// a lifecycle of its own: it advances between deploys, on a schedule, and its history is the
/// answer to "why is my site not on my domain yet". A config field could hold the name but could
/// never hold that.
/// </remarks>
public class ProjectDomain
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Which part of the project this domain fronts — one Coolify application.</summary>
    public Guid DeployTargetId { get; set; }

    /// <summary>ASCII (punycode), lowercased, no scheme, no trailing dot. What is sent to providers and resolvers.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>What the user typed, kept so a non-ASCII domain is shown back the way they wrote it.</summary>
    public string DisplayHostname { get; set; } = string.Empty;

    public DomainSource Source { get; set; } = DomainSource.UserProvided;

    public DomainStatus Status { get; set; } = DomainStatus.Pending;

    /// <summary>Whether this is the domain the project's own UI should link to.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>The server address the record has to resolve to, recorded so later checks compare against the same answer.</summary>
    public string? ExpectedAddress { get; set; }

    /// <summary>Set when DeployAI wrote the record itself, so teardown can release what it created.</summary>
    public Guid? DnsCredentialId { get; set; }

    public string? ZoneId { get; set; }

    public string? ManagedRecordId { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>
    /// When to stop waiting. Stored rather than derived from the attempt count so that restarting
    /// the API resumes the same clock instead of granting the domain a fresh hour.
    /// </summary>
    public DateTimeOffset? DeadlineAt { get; set; }

    /// <summary>
    /// The last check that actually concluded something. Separate from the most recent check
    /// because it is what decides which terminal state a deadline lands in: a run of inconclusive
    /// checks must not be reported as a DNS failure.
    /// </summary>
    public DomainStatus? LastConclusiveStatus { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>The serialized DNS check and certificate inspection behind the current status.</summary>
    public string? ObservationsJson { get; set; }

    public string? CertificateIssuer { get; set; }

    public DateTimeOffset? CertificateNotAfter { get; set; }

    /// <summary>What to tell the user. Never an exception's text.</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// The attempt a routing deploy was last triggered for. Guards the loop where the reconciler
    /// triggers a deploy, the deploy's own post-deploy hook re-assigns the domain, and the
    /// reconciler wakes to trigger another.
    /// </summary>
    public int? RoutingDeployTriggeredForAttempt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Project Project { get; set; } = null!;

    public DeployTarget DeployTarget { get; set; } = null!;
}
