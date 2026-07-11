namespace DeployAI.Core.Providers;

public enum CoolifyBuildPack
{
    Nixpacks,
    Static,
    Dockerfile,
    DockerCompose,
    Railpack
}

public static class CoolifyBuildPackValues
{
    public const string Nixpacks = "nixpacks";
    public const string Static = "static";
    public const string Dockerfile = "dockerfile";
    public const string DockerCompose = "dockercompose";
    public const string Railpack = "railpack";

    public static string ToApiValue(CoolifyBuildPack buildPack) => buildPack switch
    {
        CoolifyBuildPack.Nixpacks => Nixpacks,
        CoolifyBuildPack.Static => Static,
        CoolifyBuildPack.Dockerfile => Dockerfile,
        CoolifyBuildPack.DockerCompose => DockerCompose,
        CoolifyBuildPack.Railpack => Railpack,
        _ => throw new ArgumentOutOfRangeException(nameof(buildPack), buildPack, "Unknown Coolify build pack.")
    };

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
