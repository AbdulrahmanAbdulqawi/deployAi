namespace DeployAI.Core.Deployments.Graph;

/// <summary>
/// A relationship between two services. Edges carry the *contract* of the relationship, not
/// just connectivity — generators need those details, and before this model they were buried
/// in framework-keyed templates (nginx buffering and body-size limits lived in nginx.conf.tpl
/// even though they are properties of the route, not of nginx).
/// </summary>
public abstract record ServiceEdge(string FromServiceId, string ToServiceId);

/// <summary>
/// From forwards a path prefix to To (the single-origin "/api" hop). The attributes here are
/// what yemenBreeze needed in practice: streaming large uploads (`proxy_request_buffering off`)
/// and a body-size ceiling matched to the app's own upload limit.
/// </summary>
public sealed record ProxyRouteEdge(
    string FromServiceId,
    string ToServiceId,
    string PathPrefix,
    /// <summary>Strip the prefix before forwarding; false for /api → the API expects the prefix.</summary>
    bool StripPrefix = false,
    /// <summary>False streams request bodies through (large uploads); true lets the proxy buffer.</summary>
    bool RequestBuffering = false,
    /// <summary>Max request body ("210m"); null means the proxy's default (nginx: 1m — usually wrong).</summary>
    string? MaxBodySize = null,
    /// <summary>Upgrade websocket connections on this route (SignalR, live reload).</summary>
    bool WebSockets = false)
    : ServiceEdge(FromServiceId, ToServiceId);

/// <summary>
/// From consumes To (a database, cache, or bucket) and needs env vars to reach it.
/// </summary>
public sealed record ConnectsToEdge(
    string FromServiceId,
    string ToServiceId,
    /// <summary>env key on From → what it carries ("ConnectionStrings__Default" → connection string).</summary>
    IReadOnlyDictionary<string, string> EnvMapping,
    ConnectionCredentialKind CredentialKind,
    /// <summary>
    /// Whether missing wiring breaks the app. yemenBreeze's database connection is fatal; its
    /// S3 wiring silently degrades to local disk — a *worse* failure mode, because nothing
    /// errors while uploads land on a filesystem that vanishes on redeploy.
    /// </summary>
    bool Required = true)
    : ServiceEdge(FromServiceId, ToServiceId);

public enum ConnectionCredentialKind
{
    None,
    ConnectionString,
    S3Keys
}

/// <summary>
/// From must not start before To. HealthGated distinguishes "started" from "answers health" —
/// compose renders these differently (`depends_on` vs `condition: service_healthy`).
/// </summary>
public sealed record DependsOnEdge(
    string FromServiceId,
    string ToServiceId,
    bool HealthGated = false)
    : ServiceEdge(FromServiceId, ToServiceId);
