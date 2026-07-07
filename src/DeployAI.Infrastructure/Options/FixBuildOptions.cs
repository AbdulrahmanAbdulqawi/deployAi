namespace DeployAI.Infrastructure.Options;

public sealed class FixBuildOptions
{
    public const string SectionName = "FixBuild";
    public bool Enabled { get; set; } = true;
    public int TimeoutMinutes { get; set; } = 10;
    public int MaxOutputChars { get; set; } = 32000;
}
