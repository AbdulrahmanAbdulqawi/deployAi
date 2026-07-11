namespace DeployAI.Core.Deployments;

public enum DeploymentPlanKind
{
    Default,
    CoolifyFullStack,
    CoolifySingle
}

public static class DeploymentPlanKindValues
{
    public const string Default = "default";
    public const string CoolifyFullStack = "coolify-fullstack";
    public const string CoolifySingle = "coolify-single";

    public static string ToApiValue(DeploymentPlanKind kind) => kind switch
    {
        DeploymentPlanKind.CoolifyFullStack => CoolifyFullStack,
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

        if (string.Equals(value, CoolifySingle, StringComparison.OrdinalIgnoreCase))
        {
            kind = DeploymentPlanKind.CoolifySingle;
            return true;
        }

        kind = DeploymentPlanKind.Default;
        return string.IsNullOrWhiteSpace(value) ||
               string.Equals(value, Default, StringComparison.OrdinalIgnoreCase);
    }
}
