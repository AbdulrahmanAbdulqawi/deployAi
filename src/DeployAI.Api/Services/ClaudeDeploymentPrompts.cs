using System.Text;

using System.Text.Json;

using DeployAI.Core.Deployments;



namespace DeployAI.Api.Services;



internal static class ClaudeDeploymentPrompts

{

    private const string JsonOutputFormat =

        "Call the submit_deployment_files tool exactly once with:\n" +

        "{ \"files\": [{ \"path\": \"relative/path/from/repo/root\", \"content\": \"full file contents\" }] }\n" +

        "Do NOT return JSON as plain text or markdown fences.";



    internal static string BuildFixPrompt(

        string owner,

        string repo,

        string gitRef,

        string providerName,

        string? framework,

        DeploymentFailureAnalysis failureAnalysis)

    {

        var builder = new StringBuilder();

        builder.AppendLine("You are a deployment engineer fixing a failed build or publish on a cloud hosting platform.");

        builder.AppendLine();

        builder.AppendLine("## Context");

        builder.AppendLine($"- Repository: {owner}/{repo} @ {gitRef}");

        builder.AppendLine($"- Failed provider: {providerName}");

        if (!string.IsNullOrWhiteSpace(framework))

        {

            builder.AppendLine($"- Framework: {framework}");

        }



        builder.AppendLine($"- Failure category: {FormatFailureCategory(failureAnalysis.Category)}");
        if (failureAnalysis.ErrorCount > 1)
        {
            builder.AppendLine($"- Distinct build errors in log: {failureAnalysis.ErrorCount}");
        }

        builder.AppendLine();
        var errorHeading = failureAnalysis.ErrorCount > 1
            ? $"## Build errors ({failureAnalysis.ErrorCount} distinct errors)"
            : "## Build errors";
        builder.AppendLine(errorHeading);
        builder.AppendLine(failureAnalysis.ErrorExcerpt ?? failureAnalysis.Summary);

        if (failureAnalysis.ReferencedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Files referenced in errors");
            foreach (var path in failureAnalysis.ReferencedFiles)
            {
                builder.AppendLine($"- {path}");
            }
        }

        var actionableFix = BuildErrorActionHints.BuildSection(failureAnalysis.ErrorExcerpt ?? failureAnalysis.Summary);
        if (!string.IsNullOrWhiteSpace(actionableFix))
        {
            builder.AppendLine();
            builder.AppendLine(actionableFix);
        }

        builder.AppendLine();
        builder.AppendLine("## Task");
        if (failureAnalysis.ErrorCount > 1)
        {
            builder.AppendLine(
                $"Fix ALL {failureAnalysis.ErrorCount} errors listed above in a single change. " +
                "Use GitHub tools to read every affected file, then resolve every distinct error before submitting.");
        }
        else
        {
            builder.AppendLine(
                "Fix every error listed above. Use GitHub tools to read the affected file(s), " +
                "then resolve all reported issues before submitting.");
        }
        builder.AppendLine("Do not stop after fixing only the first error — the build must pass with zero remaining errors from this log.");

        builder.AppendLine();

        builder.AppendLine("## Repository tools");

        builder.AppendLine("You can inspect the repository directly using these tools:");

        builder.AppendLine("- github_list_directory: list files and folders at a repository path");

        builder.AppendLine("- github_read_file: read a file from the repository at the deployment git ref");
        builder.AppendLine("- run_local_build: run install/build against draft file contents to verify the fix");

        builder.AppendLine();

        builder.AppendLine("Use the tools to discover and read every file referenced in the build errors, plus any required build or deployment configuration. Avoid broad repository scans.");
        builder.AppendLine("- Prefer github_read_file on paths mentioned in the build error excerpt before listing directories");
        builder.AppendLine("- List directories only when you need to locate a file path that is not already in the build errors");
        builder.AppendLine("- You MUST github_read_file a path before returning modified content for that path");
        builder.AppendLine("- When multiple files have errors, read and fix each one before submitting");

        builder.AppendLine();
        builder.AppendLine("## Build verification (required)");
        builder.AppendLine("- After preparing fixes, call run_local_build with every modified file");
        builder.AppendLine("- If run_local_build fails, read the build output, fix remaining errors, and call run_local_build again");
        builder.AppendLine("- Only call submit_deployment_files after run_local_build reports BUILD SUCCEEDED");
        builder.AppendLine("- submit_deployment_files must include the same files that passed run_local_build");

        builder.AppendLine();

        builder.AppendLine("## Rules");

        builder.AppendLine("- Explore the repository with tools before proposing changes");

        builder.AppendLine("- Fix every file and issue named in the build error excerpt — do not default to angular.json, package.json, or tsconfig unless the errors explicitly reference them");
        builder.AppendLine("- If an error says a JSON property is missing, add ONLY that property — do not change outputPath, assets, styles, budgets, or fileReplacements");
        builder.AppendLine("- Never duplicate JSON keys (for example, do not emit outputPath twice in the same object)");
        builder.AppendLine("- Change only files needed to resolve all reported errors");
        builder.AppendLine("- Return the complete updated content for every file you modify (not diffs)");
        builder.AppendLine("- Preserve existing project conventions, formatting, and architecture");
        builder.AppendLine("- When editing JSON config (angular.json, tsconfig, vercel.json): keep valid JSON, preserve sibling keys and array structure, and make the smallest edit needed per error");
        builder.AppendLine("- In angular.json production config, fileReplacements and budgets are separate sibling arrays — never nest budgets inside fileReplacements");
        builder.AppendLine("- Do not invent secrets, API keys, or credentials; use environment variables instead");
        builder.AppendLine("- Do not refactor unrelated code or alter deployment wiring unless the errors directly involve those files");
        builder.AppendLine("- Prefer provider-native configuration (build commands, output directories, env vars, health checks) over workarounds");
        builder.AppendLine("- If an error is ambiguous, make the smallest safe fix that unblocks the build while still addressing every listed error");

        builder.AppendLine();

        builder.AppendLine("## Output format");

        builder.AppendLine("When you have fixed every error, call submit_deployment_files exactly once with all modified files:");

        builder.AppendLine(JsonOutputFormat);

        return builder.ToString();

    }



    internal static string BuildMissingFilesPrompt(

        string owner,

        string repo,

        string gitRef,

        IReadOnlyList<DeploymentPlanPart> parts,

        IReadOnlyList<MissingDeploymentFile> missingFiles)

    {

        var builder = new StringBuilder();

        builder.AppendLine("You are a deployment engineer completing split-origin deployment setup for a repository.");

        builder.AppendLine();

        builder.AppendLine("## Context");

        builder.AppendLine($"- Repository: {owner}/{repo} @ {gitRef}");

        builder.AppendLine();

        builder.AppendLine("## Deployment plan");

        AppendDeploymentPlan(builder, parts);

        builder.AppendLine();

        AppendArchitectureNotes(builder, parts);



        builder.AppendLine("## Task");

        builder.AppendLine("1. Use GitHub tools to read only the deployment-related paths you need (target ~15 tool calls, not a full repo scan).");

        builder.AppendLine("2. Compare what you read against the split-origin checklist and scan gaps below.");

        builder.AppendLine("3. Call submit_deployment_files with complete contents for every file you create or modify.");

        builder.AppendLine("4. Do not write prose or plain-text JSON — submit files through the tool only.");

        builder.AppendLine();

        builder.AppendLine("## Repository tools");

        builder.AppendLine("- github_list_directory: list files and folders at a repository path");

        builder.AppendLine("- github_read_file: read a file from the repository at the deployment git ref");

        builder.AppendLine("- submit_deployment_files: submit all generated files when ready (call once, then stop)");

        builder.AppendLine("- You MUST github_read_file a path before returning modified content for that path");

        builder.AppendLine("- Read existing Program.cs, angular.json, vercel.json, auth services, and controllers before patching them");

        builder.AppendLine("- Stop exploring as soon as you can produce the required files");

        builder.AppendLine();



        if (SplitOriginDetection.PlanUsesSplitOrigin(parts))

        {

            AppendSplitOriginChecklist(builder, parts);

        }



        builder.AppendLine("## Scan gaps (from DeployAI readiness scan)");

        foreach (var missing in missingFiles)

        {

            builder.AppendLine($"- [{missing.Severity}] {missing.Path}: {missing.Reason}");

        }



        builder.AppendLine();

        builder.AppendLine("## Rules");

        builder.AppendLine("- Address every scan gap listed above");

        builder.AppendLine("- Generate production-ready files that match the deployment plan");

        builder.AppendLine("- Use paths relative to the repository root");

        builder.AppendLine("- Align build, install, start commands, root directories, and framework defaults with each plan part");

        builder.AppendLine("- Use environment variables for URLs, secrets, and cross-service wiring — never hardcode credentials or invent deploy URLs");

        builder.AppendLine("- Match existing namespaces, project names, interceptor paths, and coding conventions from the repository");

        builder.AppendLine("- Return the complete updated content for every file you modify (not diffs)");

        builder.AppendLine("- When editing JSON config (angular.json, vercel.json): keep valid JSON, preserve sibling keys, and make the smallest edit needed");

        builder.AppendLine("- Never duplicate JSON keys (for example, do not emit outputPath twice in the same object)");

        builder.AppendLine("- In angular.json production config, fileReplacements and budgets are separate sibling arrays — never nest budgets inside fileReplacements");



        if (SplitOriginDetection.PlanUsesSplitOrigin(parts))

        {

            builder.AppendLine();

            builder.AppendLine("## Split-origin patch rules");

            builder.AppendLine("- Program.cs: replace AllowAnyOrigin() / \"AllowAll\" dev CORS with a default policy that reads AllowedOrigins from configuration, allows *.vercel.app previews via SetIsOriginAllowed, and uses AllowCredentials()");

            builder.AppendLine("- vercel.json: SPA-only rewrites to /index.html; no /api or /hubs proxy rewrites; buildCommand should run write-api-env.mjs before ng build when applicable");

            builder.AppendLine("- write-api-env.mjs: write environment.production.ts with apiBaseUrl from IDAARA_API_URL or NG_APP_API_URL env vars");

            builder.AppendLine("- api-base.interceptor.ts: prepend environment.apiBaseUrl for /api/ and /hubs/ requests");

            builder.AppendLine("- app.config.ts: register apiBaseInterceptor before other HTTP interceptors");

            builder.AppendLine("- angular.json: ensure production fileReplacements for environment.production.ts or build invokes write-api-env.mjs");

            builder.AppendLine("- HealthController.cs: use the same namespace as other controllers in the project; expose GET api/v1/health");

            builder.AppendLine("- AuthController.cs: use SameSite=None and Secure for refresh cookies in Production");

            builder.AppendLine("- auth.service.ts: send withCredentials: true on login, refresh, and logout requests");

            builder.AppendLine("- signalr.service.ts: use absolute hub URL from environment.apiBaseUrl in production");

            builder.AppendLine("- docs/DEPLOYMENT.md: document Vercel env vars (IDAARA_API_URL / NG_APP_API_URL) and Railway AllowedOrigins__0 wiring");

            builder.AppendLine("- railway.toml: reference the server Dockerfile path from the deployment plan");

        }



        builder.AppendLine();

        builder.AppendLine("## Output format");

        builder.AppendLine("When you are done exploring, call submit_deployment_files exactly once with all files:");

        builder.AppendLine(JsonOutputFormat);

        return builder.ToString();

    }



    private static void AppendSplitOriginChecklist(StringBuilder builder, IReadOnlyList<DeploymentPlanPart> parts)

    {

        var website = SplitOriginDetection.FindWebsitePart(parts);

        var server = SplitOriginDetection.FindServerPart(parts);

        if (website is null || server is null)

        {

            return;

        }



        var clientRoot = NormalizeRoot(website.RootDirectory);

        var clientPrefix = string.IsNullOrEmpty(clientRoot) ? string.Empty : $"{clientRoot}/";

        var serverRoot = NormalizeRoot(server.ServiceDirectory ?? server.RootDirectory);



        builder.AppendLine("## Split-origin checklist (Angular + Vercel + Railway)");

        builder.AppendLine("| File | Purpose |");

        builder.AppendLine("|------|---------|");

        builder.AppendLine("| railway.toml | Dockerfile-based Railway deploy |");

        builder.AppendLine($"| {clientPrefix}vercel.json | SPA-only rewrites (no /api proxy) |");

        builder.AppendLine($"| {clientPrefix}scripts/write-api-env.mjs | Injects apiBaseUrl at build time |");

        builder.AppendLine($"| {clientPrefix}src/app/core/interceptors/api-base.interceptor.ts | Rewrites /api/ and /hubs/ to Railway |");

        builder.AppendLine($"| {clientPrefix}angular.json | fileReplacements or build invokes write-api-env.mjs |");

        builder.AppendLine($"| {clientPrefix}src/app/app.config.ts | Registers apiBaseInterceptor |");

        builder.AppendLine($"| {clientPrefix}src/environments/environment.ts | Base environment with apiBaseUrl |");

        builder.AppendLine($"| {clientPrefix}src/environments/environment.production.ts | Production environment (written at build) |");

        builder.AppendLine($"| {serverRoot}/Controllers/HealthController.cs | Railway health probe |");

        builder.AppendLine($"| {serverRoot}/Program.cs | Forwarded headers + AllowedOrigins CORS + *.vercel.app |");

        builder.AppendLine($"| {serverRoot}/Controllers/AuthController.cs | SameSite=None refresh cookies in Production |");

        builder.AppendLine($"| {clientPrefix}src/app/core/services/auth.service.ts | withCredentials on auth requests |");

        builder.AppendLine($"| {clientPrefix}src/app/core/services/signalr.service.ts | Absolute hub URL in production |");

        builder.AppendLine("| docs/DEPLOYMENT.md | Team runbook for env var wiring |");

        builder.AppendLine();

    }



    private static void AppendDeploymentPlan(StringBuilder builder, IReadOnlyList<DeploymentPlanPart> parts)

    {

        foreach (var part in parts)

        {

            builder.AppendLine($"- Role: {part.Role}, Provider: {part.ProviderName}, Framework: {part.Framework ?? "unknown"}");

            if (!string.IsNullOrWhiteSpace(part.RootDirectory))

            {

                builder.AppendLine($"  Root directory: {part.RootDirectory}");

            }



            if (!string.IsNullOrWhiteSpace(part.ServiceDirectory))

            {

                builder.AppendLine($"  Service directory: {part.ServiceDirectory}");

            }



            if (!string.IsNullOrWhiteSpace(part.BuildCommand))

            {

                builder.AppendLine($"  Build command: {part.BuildCommand}");

            }



            if (!string.IsNullOrWhiteSpace(part.InstallCommand))

            {

                builder.AppendLine($"  Install command: {part.InstallCommand}");

            }



            if (!string.IsNullOrWhiteSpace(part.StartCommand))

            {

                builder.AppendLine($"  Start command: {part.StartCommand}");

            }



            if (!string.IsNullOrWhiteSpace(part.OutputDirectory))

            {

                builder.AppendLine($"  Output directory: {part.OutputDirectory}");

            }



            if (!string.IsNullOrWhiteSpace(part.DockerfilePath))

            {

                builder.AppendLine($"  Dockerfile path: {part.DockerfilePath}");

            }

        }



        builder.AppendLine();

        builder.AppendLine("Plan JSON:");

        builder.AppendLine(JsonSerializer.Serialize(parts));

    }



    private static void AppendArchitectureNotes(StringBuilder builder, IReadOnlyList<DeploymentPlanPart> parts)

    {

        builder.AppendLine("## Architecture notes");



        var website = SplitOriginDetection.FindWebsitePart(parts);

        var server = SplitOriginDetection.FindServerPart(parts);

        if (website is not null && server is not null)

        {

            builder.AppendLine(

                $"- Frontend ({website.Framework ?? "app"}) on {website.ProviderName}; " +

                $"API ({server.Framework ?? "server"}) on {server.ProviderName}");

        }



        if (SplitOriginDetection.PlanUsesSplitOrigin(parts))

        {

            builder.AppendLine("- Architecture: split-origin (browser talks to API host directly, not through the frontend host)");

        }

        else if (parts.Count > 1)

        {

            builder.AppendLine("- Architecture: multi-part deployment; wire services using environment variables and documented URLs");

        }

        else

        {

            builder.AppendLine("- Architecture: single-part deployment");

        }



        builder.AppendLine();

    }



    private static string NormalizeRoot(string? root) =>

        root?.Trim().Trim('/') ?? string.Empty;



    private static string FormatFailureCategory(DeploymentFailureCategory category) =>

        category switch

        {

            DeploymentFailureCategory.CodeBuild => "code/build error",

            DeploymentFailureCategory.Infrastructure => "infrastructure/platform error",

            DeploymentFailureCategory.Unknown => "unknown",

            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled failure category.")

        };

}


