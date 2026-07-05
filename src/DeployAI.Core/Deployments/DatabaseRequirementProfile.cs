namespace DeployAI.Core.Deployments;

public sealed record DatabaseRequirementProfile(
    bool RequiresPostgres,
    bool RequiresRedis,
    IReadOnlyList<string> ConnectionStringKeys,
    string? PostgresDatabaseName = null);
