namespace DeployAI.Core.Deployments;

/// <summary>
/// Detected database requirements for a repo (from docker-compose, appsettings.json connection
/// strings, or a Prisma schema) - drives whether to offer auto-provisioning Postgres/Redis.
/// </summary>
/// <param name="IsInconclusive">The repository could not be read, so nothing was detected either
/// way. Never the same as "needs no database": one of those is safe to deploy on. An app that
/// silently gets no database meets an empty connection string and fails at its first query, which
/// looks nothing like a scan that could not run.</param>
public sealed record DatabaseRequirementProfile(
    bool RequiresPostgres,
    bool RequiresRedis,
    IReadOnlyList<string> ConnectionStringKeys,
    string? PostgresDatabaseName = null,
    bool IsInconclusive = false);
