namespace DeployAI.Api.Services;

internal static class DeploymentVerificationCheckMetadata
{
    internal static bool CanRequestClaudeFix(string status, string? suggestedAction) =>
        status is "failed" or "warning" &&
        suggestedAction is not "redeploy_website" and not "redeploy_server";

    internal static IReadOnlyList<string> ResolveReferencedFiles(string checkId, string? suggestedAction)
    {
        if (checkId.StartsWith("website.reachable", StringComparison.Ordinal) ||
            suggestedAction == "fix_output_directory")
        {
            return ["vercel.json", "angular.json"];
        }

        if (checkId == "website.spa_shell")
        {
            return ["vercel.json", "angular.json", "index.html"];
        }

        if (checkId == "website.split_origin_wiring")
        {
            return
            [
                "vercel.json",
                "angular.json",
                "src/app/app.config.ts",
                "src/environments/environment.production.ts",
                "src/environments/environment.ts"
            ];
        }

        if (checkId.StartsWith("connection.", StringComparison.Ordinal))
        {
            return
            [
                "vercel.json",
                "src/app/app.config.ts",
                "src/environments/environment.production.ts",
                "appsettings.json",
                "Program.cs"
            ];
        }

        if (checkId.StartsWith("server.", StringComparison.Ordinal))
        {
            return ["Program.cs", "appsettings.json", "railway.toml"];
        }

        return ["vercel.json"];
    }
}
