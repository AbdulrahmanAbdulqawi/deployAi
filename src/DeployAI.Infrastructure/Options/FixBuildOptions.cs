namespace DeployAI.Infrastructure.Options;

public sealed class FixBuildOptions
{
    public const string SectionName = "FixBuild";

    /// <summary>
    /// When true, the fix agent may call run_command (e.g. dotnet build) during its tool loop.
    /// Commands execute in the fix workspace on the DeployAI host.
    /// </summary>
    public bool AllowAgentBuildCommands { get; set; } = true;

    /// <summary>
    /// When true, DeployAI runs an extra build on the host after Claude submits, before committing the fix.
    /// Independent of <see cref="AllowAgentBuildCommands"/>.
    /// </summary>
    public bool VerifyOnSubmitLocally { get; set; }

    public int TimeoutMinutes { get; set; } = 10;
    public int MaxOutputChars { get; set; } = 32000;
}
