namespace DeployAI.Infrastructure.Options;

public class AppOptions
{
    public const string SectionName = "App";
    public string FrontendUrl { get; set; } = "http://localhost:4200";
    public string ApiUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// A zone DeployAI controls, with a wildcard A record pointing at the deployment server. Set it
    /// and every app can be offered a working HTTPS name — <c>myapp.apps.example.com</c> — with no
    /// DNS work from the user at all, which is the only path to a certificate that asks nothing of
    /// someone who does not know what an A record is.
    /// </summary>
    /// <remarks>
    /// Left empty by default because it cannot be invented: it needs a domain that is actually
    /// owned and a wildcard record that actually exists. Empty means the offer is simply not made,
    /// and apps keep falling back to the server's generated address.
    /// <para>
    /// One wildcard points at one address, so this serves a single deployment server. A second
    /// server needs per-app records through a connected DNS account instead.
    /// </para>
    /// </remarks>
    public string PlatformDomain { get; set; } = string.Empty;
}
