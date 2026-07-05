namespace DeployAI.Infrastructure.Options;

public class EncryptionOptions
{
    public const string SectionName = "Encryption";
    public string Key { get; set; } = string.Empty;
}
