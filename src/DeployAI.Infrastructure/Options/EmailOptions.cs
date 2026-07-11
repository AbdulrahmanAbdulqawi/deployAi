namespace DeployAI.Infrastructure.Options;

public class EmailOptions
{
    public const string SectionName = "Email";
    public bool Enabled { get; set; }
    public string FromAddress { get; set; } = "deployai@localhost";
    public string FromName { get; set; } = "DeployAI";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool UseSsl { get; set; } = true;
}
