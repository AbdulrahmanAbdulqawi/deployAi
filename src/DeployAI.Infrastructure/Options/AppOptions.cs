namespace DeployAI.Infrastructure.Options;

public class AppOptions
{
    public const string SectionName = "App";
    public string FrontendUrl { get; set; } = "http://localhost:4200";
    public string ApiUrl { get; set; } = "http://localhost:5000";
}
