namespace DeployAI.Core.Deployments;

public enum DeploymentTriggeredBy
{
    User,
    Webhook
}

public static class DeploymentTriggeredByValues
{
    public const string User = "user";
    public const string Webhook = "webhook";

    public static string ToApiValue(DeploymentTriggeredBy triggeredBy) =>
        triggeredBy switch
        {
            DeploymentTriggeredBy.User => User,
            DeploymentTriggeredBy.Webhook => Webhook,
            _ => User
        };

    public static DeploymentTriggeredBy FromApiValue(string? value) =>
        string.Equals(value, Webhook, StringComparison.OrdinalIgnoreCase)
            ? DeploymentTriggeredBy.Webhook
            : DeploymentTriggeredBy.User;
}
