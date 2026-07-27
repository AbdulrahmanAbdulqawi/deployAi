namespace DeployAI.Core.Providers;

/// <summary>The build strategies Coolify supports for an application.</summary>
public enum CoolifyBuildPack
{
    /// <summary>Auto-detected build (install + build + serve), used for anything with a build command.</summary>
    Nixpacks,
    /// <summary>Raw file copy with no build step - only correct when there's nothing to compile.</summary>
    Static,
    Dockerfile,
    DockerCompose,
    Railpack
}

/// <summary>String constants and conversions for Coolify's <c>build_pack</c> API values.</summary>
public static class CoolifyBuildPackValues
{
    public const string Nixpacks = "nixpacks";
    public const string Static = "static";
    public const string Dockerfile = "dockerfile";
    public const string DockerCompose = "dockercompose";
    public const string Railpack = "railpack";

    /// <summary>Converts a <see cref="CoolifyBuildPack"/> enum value to Coolify's API string.</summary>
    public static string ToApiValue(CoolifyBuildPack buildPack) => buildPack switch
    {
        CoolifyBuildPack.Nixpacks => Nixpacks,
        CoolifyBuildPack.Static => Static,
        CoolifyBuildPack.Dockerfile => Dockerfile,
        CoolifyBuildPack.DockerCompose => DockerCompose,
        CoolifyBuildPack.Railpack => Railpack,
        _ => throw new ArgumentOutOfRangeException(nameof(buildPack), buildPack, "Unknown Coolify build pack.")
    };

    /// <summary>Parses a Coolify build_pack API string back into the <see cref="CoolifyBuildPack"/> enum, case-insensitively.</summary>
    public static bool TryParse(string? value, out CoolifyBuildPack buildPack)
    {
        if (string.Equals(value, Nixpacks, StringComparison.OrdinalIgnoreCase))
        {
            buildPack = CoolifyBuildPack.Nixpacks;
            return true;
        }

        if (string.Equals(value, Static, StringComparison.OrdinalIgnoreCase))
        {
            buildPack = CoolifyBuildPack.Static;
            return true;
        }

        if (string.Equals(value, Dockerfile, StringComparison.OrdinalIgnoreCase))
        {
            buildPack = CoolifyBuildPack.Dockerfile;
            return true;
        }

        if (string.Equals(value, DockerCompose, StringComparison.OrdinalIgnoreCase))
        {
            buildPack = CoolifyBuildPack.DockerCompose;
            return true;
        }

        if (string.Equals(value, Railpack, StringComparison.OrdinalIgnoreCase))
        {
            buildPack = CoolifyBuildPack.Railpack;
            return true;
        }

        buildPack = default;
        return false;
    }
}
