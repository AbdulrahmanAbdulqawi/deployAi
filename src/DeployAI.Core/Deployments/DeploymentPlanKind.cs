namespace DeployAI.Core.Deployments;

/// <summary>The overall shape of a classified deployment plan - a normal multi-part plan, or a Coolify-specific single/full-stack shape.</summary>
public enum DeploymentPlanKind
{
    Default,
    /// <summary>Website and server both deployed as separate Coolify applications on the same instance.</summary>
    CoolifyFullStack,
    /// <summary>A single Coolify application (no separate website/server split).</summary>
    CoolifySingle
}

/// <summary>String constants and conversions for <see cref="DeploymentPlanKind"/> API values.</summary>
public static class DeploymentPlanKindValues
{
    public const string Default = "default";
    public const string CoolifyFullStack = "coolify-fullstack";
    public const string CoolifySingle = "coolify-single";

    /// <summary>Converts a <see cref="DeploymentPlanKind"/> enum value to its API string.</summary>
    public static string ToApiValue(DeploymentPlanKind kind) => kind switch
    {
        DeploymentPlanKind.CoolifyFullStack => CoolifyFullStack,
        DeploymentPlanKind.CoolifySingle => CoolifySingle,
        _ => Default
    };

    /// <summary>Parses an API string back into the <see cref="DeploymentPlanKind"/> enum, defaulting to <see cref="DeploymentPlanKind.Default"/>.</summary>
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
