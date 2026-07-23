namespace DeployAI.Core.Deployments;

public enum DeploymentPlanKind
{
    Default,
    CoolifyFullStack,

    /// <summary>
    /// One Docker Compose resource on Coolify serving the whole app from a single origin —
    /// typically a web service that proxies /api to an internal API service. Distinct from
    /// <see cref="CoolifyFullStack"/>, which is two separate Coolify applications on two
    /// domains wired together with cross-origin env vars.
    /// </summary>
    CoolifyCompose,
    CoolifySingle
}

public static class DeploymentPlanKindValues
{
    public const string Default = "default";
    public const string CoolifyFullStack = "coolify-fullstack";
    public const string CoolifyCompose = "coolify-compose";
    public const string CoolifySingle = "coolify-single";

    public static string ToApiValue(DeploymentPlanKind kind) => kind switch
    {
        DeploymentPlanKind.CoolifyFullStack => CoolifyFullStack,
        DeploymentPlanKind.CoolifyCompose => CoolifyCompose,
        DeploymentPlanKind.CoolifySingle => CoolifySingle,
        _ => Default
    };

    public static bool TryParse(string? value, out DeploymentPlanKind kind)
    {
        if (string.Equals(value, CoolifyFullStack, StringComparison.OrdinalIgnoreCase))
        {
            kind = DeploymentPlanKind.CoolifyFullStack;
            return true;
        }

        if (string.Equals(value, CoolifyCompose, StringComparison.OrdinalIgnoreCase))
        {
            kind = DeploymentPlanKind.CoolifyCompose;
            return true;
        }

        if (string.Equals(value, CoolifySingle, StringComparison.OrdinalIgnoreCase))
        {
            kind = DeploymentPlanKind.CoolifySingle;
            return true;
        }

        kind = DeploymentPlanKind.Default;
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, Default, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the whole app is served from one origin, so no cross-origin API URL
    /// injection or CORS wiring is needed (and split-origin readiness checks must not run).
    /// </summary>
    public static bool IsSingleOrigin(DeploymentPlanKind kind) =>
        kind == DeploymentPlanKind.CoolifyCompose;
}
