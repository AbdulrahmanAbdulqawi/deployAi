namespace DeployAI.Infrastructure.Options;

public class RailwayOptions
{
    public const string SectionName = "Railway";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = "/api/auth/railway/callback";
    public string Scopes { get; set; } = "openid email profile offline_access workspace:admin";
}
