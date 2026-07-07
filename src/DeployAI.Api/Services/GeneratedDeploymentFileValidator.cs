using System.Text.Json;
using DeployAI.Core.Deployments;
using DeployAI.Core.Exceptions;



namespace DeployAI.Api.Services;



internal static class GeneratedDeploymentFileValidator

{

    internal static void ValidateOrThrow(IReadOnlyList<(string Path, string Content)> files)

    {

        foreach (var (path, content) in files)

        {

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))

            {

                try

                {

                    JsonDocument.Parse(content);

                }

                catch (JsonException)

                {

                    throw new DeployAIException(

                        "setup_generation_failed",

                        $"Claude returned invalid JSON for {path}. Generate the setup again or edit that file manually.");

                }



                ValidateAngularJsonHeuristics(path, content);



                if (path.EndsWith("vercel.json", StringComparison.OrdinalIgnoreCase) &&

                    VercelJsonRewrites.HasApiProxyAntiPattern(content))

                {

                    throw new DeployAIException(

                        "setup_generation_failed",

                        "Claude returned vercel.json with /api or /hubs proxy rewrites. Split-origin apps must call Railway directly.");

                }

            }



            if (path.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) &&

                HasDevOnlyCorsPolicy(content) &&

                !HasSplitOriginCorsSetup(content))

            {

                throw new DeployAIException(

                    "setup_generation_failed",

                    "Claude returned Program.cs with AllowAnyOrigin() or AllowAll CORS instead of AllowedOrigins split-origin setup.");

            }

        }

    }



    internal static void ValidateOrThrow(IReadOnlyList<GeneratedDeploymentFile> files) =>

        ValidateOrThrow(files.Select(file => (file.Path, file.Content)).ToArray());



    private static void ValidateAngularJsonHeuristics(string path, string content)
    {
        if (!path.EndsWith("angular.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (CountOccurrences(content, "\"outputPath\"") > 1)
        {
            throw new DeployAIException(
                "setup_generation_failed",
                "Claude returned angular.json with duplicate outputPath entries. Generate the setup again.");
        }

        using var document = JsonDocument.Parse(content);
        if (HasBudgetsNestedInFileReplacements(document.RootElement))
        {
            throw new DeployAIException(
                "setup_generation_failed",
                "Claude returned angular.json with budgets nested inside fileReplacements. Generate the setup again.");
        }
    }

    private static bool HasBudgetsNestedInFileReplacements(JsonElement root)
    {
        foreach (var productionConfig in FindProductionBuildConfigurations(root))
        {
            if (!productionConfig.TryGetProperty("fileReplacements", out var replacements) ||
                replacements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in replacements.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in entry.EnumerateObject())
                {
                    if (string.Equals(property.Name, "budgets", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static IEnumerable<JsonElement> FindProductionBuildConfigurations(JsonElement root)
    {
        if (!root.TryGetProperty("projects", out var projects) ||
            projects.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var project in projects.EnumerateObject())
        {
            if (project.Value.TryGetProperty("architect", out var architect) &&
                architect.TryGetProperty("build", out var build) &&
                build.TryGetProperty("configurations", out var configurations) &&
                configurations.TryGetProperty("production", out var production))
            {
                yield return production;
            }
        }
    }



    private static bool HasDevOnlyCorsPolicy(string content) =>

        content.Contains("AllowAnyOrigin()", StringComparison.Ordinal) ||

        content.Contains("\"AllowAll\"", StringComparison.OrdinalIgnoreCase);



    private static bool HasSplitOriginCorsSetup(string content) =>

        content.Contains("AllowedOrigins", StringComparison.OrdinalIgnoreCase) &&

        (content.Contains("SetIsOriginAllowed", StringComparison.OrdinalIgnoreCase) ||

         content.Contains("vercel.app", StringComparison.OrdinalIgnoreCase));



    private static int CountOccurrences(string text, string value)

    {

        var count = 0;

        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)

        {

            count++;

            index += value.Length;

        }



        return count;

    }

}


