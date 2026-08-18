namespace DeployAI.Core.Providers;

/// <summary>Whether the application a deploy target points at is still there.</summary>
public enum ProviderApplicationPresence
{
    /// <summary>DeployAI could not find out. Says nothing about the application.</summary>
    Unknown = 0,

    /// <summary>The provider still has it.</summary>
    Present = 1,

    /// <summary>The provider says it does not exist. Someone deleted it.</summary>
    Absent = 2
}

/// <summary>
/// What the provider says about an application DeployAI believes it deployed.
/// </summary>
/// <param name="State">The provider's own word for what it is doing, when present. Not normalised.</param>
/// <param name="Detail">Always populated, and for <see cref="ProviderApplicationPresence.Unknown"/> it must say what got in the way.</param>
public sealed record ProviderApplicationExistence(
    ProviderApplicationPresence Presence,
    string? State,
    string? DeployUrl,
    string Detail)
{
    public bool IsInconclusive => Presence is ProviderApplicationPresence.Unknown;
}

/// <summary>
/// Answers "is this application still there", separately from "what is it doing".
/// </summary>
/// <remarks>
/// <para>
/// A capability of its own rather than a field on <c>ProviderServiceStatus</c>, because that type
/// cannot answer this honestly. Coolify's <c>TryGetApplicationAsync</c> returns null for every
/// non-2xx and <c>GetServiceStatusAsync</c> turns that into the string <c>"unknown"</c> — so a 404
/// from a deleted application and a 500 from an unreachable instance are byte-identical. Railway's
/// service status has the same shape.
/// </para>
/// <para>
/// Those are opposite answers. One means the application was deleted and DeployAI's dashboard is
/// advertising a dead link; the other means DeployAI could not look and knows nothing. Building the
/// existence check on a type that conflates them would bake the wrong answer in at the source, which
/// is why this interface exists rather than a new field.
/// </para>
/// </remarks>
public interface IProviderApplicationExistence
{
    string ProviderName { get; }

    Task<ProviderApplicationExistence> CheckApplicationExistsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken);
}

public interface IProviderApplicationExistenceFactory
{
    IProviderApplicationExistence? GetApplicationExistence(string providerName);
}
