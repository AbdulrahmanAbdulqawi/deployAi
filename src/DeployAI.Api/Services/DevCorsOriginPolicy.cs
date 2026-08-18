namespace DeployAI.Api.Services;

/// <summary>
/// Local dev routinely has more than one `ng serve` running at once — concurrent sessions on the
/// same machine each grab whatever port is free (4200, 4201, 4202, ...). Matching any localhost
/// port fixes the whole class of "the frontend moved to a new port again" instead of a single
/// hardcoded origin that needs editing every time a new port gets used. Never applied outside
/// Development — production/deployed environments keep the strict, single configured origin.
/// </summary>
public static class DevCorsOriginPolicy
{
    public static bool IsLocalDevOrigin(string? origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        return originUri.Host is "localhost" or "127.0.0.1";
    }
}
