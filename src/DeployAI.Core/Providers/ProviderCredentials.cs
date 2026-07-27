namespace DeployAI.Core.Providers;

/// <summary>
/// A decrypted provider auth token, passed to provider clients per-call. For Coolify this is a
/// serialized instance-URL + API-token pair (see <see cref="CoolifyCredentialStorage"/>); for
/// other providers it's the raw token.
/// </summary>
public sealed record ProviderCredentials(string Token);
