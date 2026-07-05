namespace DeployAI.Infrastructure.Options;

public class VercelOptions
{
    public const string SectionName = "Vercel";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string IntegrationSlug { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = "/api/auth/vercel/callback";
}
