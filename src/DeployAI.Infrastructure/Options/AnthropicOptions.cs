namespace DeployAI.Infrastructure.Options;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-6";
    public int FixAgentMaxTurns { get; set; } = 50;
    public int FixMaxToolCalls { get; set; } = 100;
    public int FixMaxCommands { get; set; } = 40;
    public int FixForceSubmitAfterToolCalls { get; set; } = 35;
    public int SetupAgentMaxTurns { get; set; } = 22;
    public int SetupMaxToolCalls { get; set; } = 35;
    public int SetupForceSubmitAfterToolCalls { get; set; } = 18;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public int AgentRequestTimeoutMinutes { get; set; } = 45;
    public int MaxOutputTokens { get; set; } = 16384;
}
