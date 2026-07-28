namespace DeployAI.Core.Deployments;

/// <summary>
/// Detected database requirements for a repo (from docker-compose, appsettings.json connection
/// strings, or a Prisma schema) - drives whether to offer auto-provisioning Postgres/Redis.
/// </summary>
public sealed record DatabaseRequirementProfile(
    bool RequiresPostgres,
    bool RequiresRedis,
    IReadOnlyList<string> ConnectionStringKeys,
    string? PostgresDatabaseName = null);
