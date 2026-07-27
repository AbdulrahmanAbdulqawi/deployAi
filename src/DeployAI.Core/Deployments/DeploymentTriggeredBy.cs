namespace DeployAI.Core.Deployments;

/// <summary>Who/what started a deployment - a user action, or an inbound GitHub push webhook (auto-deploy).</summary>
public enum DeploymentTriggeredBy
{
    User,
    Webhook
}

/// <summary>String constants and conversions for <see cref="DeploymentTriggeredBy"/> values as stored on a deployment.</summary>
public static class DeploymentTriggeredByValues
{
    public const string User = "user";
    public const string Webhook = "webhook";

    /// <summary>Converts a <see cref="DeploymentTriggeredBy"/> enum value to its stored string.</summary>
    public static string ToApiValue(DeploymentTriggeredBy triggeredBy) =>
        triggeredBy switch
        {
            DeploymentTriggeredBy.User => User,
            DeploymentTriggeredBy.Webhook => Webhook,
            _ => User
        };

    /// <summary>Parses a stored string back into the <see cref="DeploymentTriggeredBy"/> enum, defaulting to <see cref="DeploymentTriggeredBy.User"/>.</summary>
    public static DeploymentTriggeredBy FromApiValue(string? value) =>
        string.Equals(value, Webhook, StringComparison.OrdinalIgnoreCase)
            ? DeploymentTriggeredBy.Webhook
            : DeploymentTriggeredBy.User;
}
