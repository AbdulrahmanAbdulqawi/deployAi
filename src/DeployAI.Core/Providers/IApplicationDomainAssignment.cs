namespace DeployAI.Core.Providers;

/// <summary>
/// What the provider says a domain is currently set to.
/// </summary>
/// <param name="CouldRead">
/// Whether the application's configuration could be read at all. False means the check did not
/// happen — which must never be reported as "the domain is missing", because the two lead to
/// opposite actions.
/// </param>
public sealed record AssignedDomainRead(bool CouldRead, IReadOnlyList<string> Domains)
{
    public static AssignedDomainRead Unavailable => new(false, []);

    public bool Includes(string domain) =>
        CouldRead && Domains.Any(assigned =>
            assigned.Contains(domain, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Attaching a domain to an already-created application, and reading back what stuck.
/// </summary>
/// <remarks>
/// Separate from the create request because a domain cannot honestly be assigned at creation: DNS
/// is unverified at that point by definition, and a domain written with an https:// scheme starts
/// a certificate challenge the moment it lands.
/// </remarks>
public interface IApplicationDomainAssignment
{
    string ProviderName { get; }

    /// <summary>
    /// Attaches <paramref name="domain"/>, scheme included, to the application. The caller decides
    /// the scheme, because the scheme is what asks for a certificate.
    /// </summary>
    Task AssignApplicationDomainAsync(
        ProviderCredentials credentials,
        string applicationUuid,
        string domain,
        string? serviceName,
        bool isCompose,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads back what the application now holds. Advisory: a provider has shipped a version where
    /// this PATCH returned success without persisting, so a read-back catches that quickly — but a
    /// certificate actually being served is the proof, and a failed read must not be mistaken for a
    /// failed assignment.
    /// </summary>
    Task<AssignedDomainRead> ReadAssignedDomainsAsync(
        ProviderCredentials credentials,
        string applicationUuid,
        bool isCompose,
        CancellationToken cancellationToken);
}

public interface IApplicationDomainAssignmentFactory
{
    IApplicationDomainAssignment? GetDomainAssignment(string providerName);
}
