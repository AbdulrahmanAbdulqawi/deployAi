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

    private const string FixInstructions =
        """
        ## Task

        1. Detect required frameworks and tools from the build log, deployment plan, and repository files (package.json, .csproj, go.mod, requirements.txt, etc.).
        2. Extract every distinct error from the build log.
        3. Categorize each error: code issue, config issue, or infrastructure issue.
        4. Read ALL affected files: use read_file for every file mentioned in ANY error.
        5. Fix code errors exhaustively: use write_file to update source code, imports, and logic to resolve ALL reported failures.
        6. Fix config errors exhaustively: use write_file to update build configs to fix ALL configuration mismatches.
        7. Document infrastructure errors: if the error is network/provider related, document the fix and provide a workaround.
        8. Verify ALL fixes: run the project's build with run_command (for example 'npm install' then 'npm run build', or 'dotnet build').
        9. Loop until zero errors: if a command fails, read the output, fix the files with write_file, and run the build again.
        10. Stop only when the build reports success (exit code 0): submit only after the build passes with zero remaining errors.

        ## Workspace Tools

        - read_file: read a file's current contents in the workspace (reflects your write_file edits)
        - list_directory: list files and folders at a path in the workspace
        - write_file: create or replace a file with full contents (only written files are committed)
        - run_command: run a shell command in the workspace (install deps, build, test); returns exit code + output
        - submit_deployment_files: submit all changed files (call once after the build succeeds, then stop)

        ## Error Classification Guide

        ### Code Errors (Fixable)

        - Missing imports or undefined references
        - Type errors (TypeScript, Python type hints, Java generics)
        - Function/method signature mismatches
        - Logic errors (off-by-one, null checks, etc.)
        - Missing or incorrect language syntax

        Action: read the file, understand the issue, fix the code.

        ### Config Errors (Fixable)

        - Missing or incorrect configuration keys in JSON, YAML, TOML
        - Wrong paths in build configs (outputPath, rootDir, srcDir, etc.)
        - Missing environment variable definitions
        - Incorrect script commands or entry points
        - Mismatched dependency versions in package.json, requirements.txt, go.mod, pom.xml, Gemfile

        Action: read the config file, add/update the required keys, validate syntax.

        ### Infrastructure Errors (Document + Escalate)

        - Network failures (timeouts, DNS resolution)
        - Image/artifact not found on registry
        - Permission/authentication issues
        - Resource limits (disk, memory, CPU)
        - Provider service outages

        Action: document the error, suggest env var or provider setting fixes, note what requires manual intervention.

        ## Rules

        ### General

        - Explore the repository with tools before proposing changes.
        - Read every file mentioned in the build error log.
        - Fix ALL errors listed — do not stop after the first fix.
        - Do not refactor unrelated code; only fix what the errors require.
        - Return complete file contents, not diffs.
        - Preserve existing code style, formatting, and project conventions.
        - Use environment variables for config, not hardcoded values.

        ### Framework & Tool Detection

        - Inspect the deployment plan for the Framework field.
        - Scan key config files to determine the framework:
          - Node.js: package.json, tsconfig.json, next.config.js, webpack.config.js, angular.json
          - .NET: *.csproj, *.sln, .NET version in files
          - Python: requirements.txt, setup.py, pyproject.toml, Pipfile
          - Go: go.mod, go.sum
          - Java: pom.xml, build.gradle, MANIFEST.MF
          - Rust: Cargo.toml, Cargo.lock
          - Ruby: Gemfile, Gemfile.lock
          - PHP: composer.json, composer.lock
        - Check the build log and deployment plan for required tools.

        ### Framework & Tool Installation

        Before building, install the project's dependencies with its package manager (language runtimes such as Node.js and .NET are already available on PATH):

        - Node.js / npm / yarn / pnpm
          - Detect the Node.js version from .nvmrc, engines in package.json, or .node-version
          - Install Node.js (latest LTS if version not specified)
          - Install npm, yarn, or pnpm based on the lock file (package-lock.json, yarn.lock, pnpm-lock.yaml)
        - .NET / C#
          - Detect the .NET version from .csproj, global.json, or error messages
          - Install the .NET SDK (matching version, or latest if not specified)
          - Ensure the dotnet CLI is available: dotnet --version
        - Python
          - Detect the Python version from requirements.txt, setup.py, .python-version, or pyproject.toml
          - Install Python (3.9+ recommended if not specified)
          - Install pip or Poetry based on the package manager file
        - Go
          - Detect the Go version from go.mod or build errors
          - Install the Go SDK (matching version, or latest if not specified)
          - Ensure the go CLI is available: go version
        - Java
          - Detect the Java version from pom.xml, build.gradle, or MANIFEST.MF
          - Install the JDK (matching version, or Java 17 LTS if not specified)
          - Install Maven or Gradle based on the build file
        - Rust
          - Detect the Rust version from Cargo.toml or rust-toolchain
          - Install the Rust toolchain via rustup
          - Ensure cargo is available: cargo --version
        - Build tools
          - Make: install if a Makefile exists
          - Docker: install if a Dockerfile path is in the deployment plan
          - Git: install if repository cloning is needed

        Installation strategy:

        1. Parse the deployment plan's Framework field.
        2. Read key framework config files (package.json, .csproj, go.mod, etc.).
        3. Detect version requirements (explicit or implicit).
        4. Install SDKs, runtimes, and package managers in dependency order.
        5. Verify installation with version checks (node --version, dotnet --version, python --version, etc.).
        6. Proceed to the build with run_command only after dependencies are installed and verified.

        ### Reading Files

        - You MUST read_file a path before modifying it with write_file.
        - If a file is mentioned in the error but does not exist, check if it should be created or if the path is wrong.
        - Use list_directory only if you need to locate a file path not in the error log.

        ### Fixing Code

        - Make minimal, surgical edits to resolve the error.
        - Do not refactor imports, classes, or functions unless they directly cause the error.
        - Preserve whitespace and formatting conventions from the existing file.
        - Fix type errors by updating type annotations or implementations, not by removing types.
        - Fix missing imports by adding the correct import statement.

        ### Fixing Config

        - Keep valid JSON, YAML, TOML, or other config syntax.
        - Preserve all sibling keys and existing configuration.
        - Add only the missing or incorrect keys.
        - Do not duplicate config keys.
        - For JSON: use the indentation and quote style from the existing file.
        - For package.json/requirements.txt/go.mod: resolve dependency version conflicts, do not remove entries.

        ### Verification

        - After writing fixes with write_file, run the build with run_command.
        - If a command fails, read the output, fix remaining errors with write_file, and run it again.
        - Do not submit unless the build reports success (exit code 0).
        - Confirm the build output directory contains expected artifacts (if applicable).

        ### Infrastructure Issues

        If the error is infrastructure-related (network, auth, resource limits):

        - Document the issue clearly.
        - Suggest environment variables or provider settings that might help.
        - Note what requires manual intervention (e.g., provider console, secret manager).
        - Do not attempt to "fix" infrastructure by changing code.
        - If the error blocks the build irreversibly, report it and request escalation.

        ### Cascading & Hidden Errors

        After the first build run:

        - New errors may appear that were hidden by earlier failures.
        - Fix these new errors in subsequent passes.
        - Read the new error output carefully — it may point to different files or root causes.
        - Keep looping until the build passes with zero errors.

        ## Workflow (Multi-Pass Error Fixing)

        ### Pass 0: Detect and Install Frameworks

        1. Detect required frameworks (deployment plan Framework field, repository config files, version requirements).
        2. Install frameworks and tools in order (SDKs and runtimes, then package managers, then build tools).
        3. Verify installation with version checks.
        4. Proceed only after verification — all frameworks must be installed before Pass 1.

        ### Pass 1: Extract and Read

        1. Extract every distinct error from the build log (stack traces, error messages, compiler output).
        2. Build a map of file to errors (one file may have multiple errors).
        3. Read ALL affected files using read_file for each file.
        4. Understand dependencies: if fixing file A requires changing file B, read file B too.

        ### Pass 2: Fix All Errors

        1. For each error, identify the root cause, determine which file(s) need changes, and make the minimal fix.
        2. Fix code errors (imports, types, logic, syntax).
        3. Fix config errors (JSON/YAML/TOML keys, paths, versions, environment variables).
        4. Document infrastructure errors.
        5. Prepare all modified files for submission.

        ### Pass 3: Verify and Loop

        1. Write all fixes with write_file, then run the build with run_command.
        2. If it succeeds (exit code 0), submit. If it fails, fix remaining errors.
        3. When fixing remaining errors, read the new error output, identify errors not in the original log (cascading/hidden), fix each, and verify again.
        4. Once the build reports success, call submit_deployment_files with all changed files.

        ## Key Principles

        - Never stop after one pass: the first build attempt often reveals cascading errors.
        - Always loop until success: keep fixing and rebuilding until the exit code is 0.
        - Read before fixing: do not guess at file structure; read the actual file first.
        - Fix exhaustively: address every error in the log, not just the first one.
        - Preserve intent: fix only what the errors require; do not refactor unrelated code.

        ## What NOT to Do

        - Do NOT skip framework installation — install all required SDKs, runtimes, and package managers before building.
        - Do NOT assume frameworks are already installed — verify with version checks.
        - Do NOT guess framework versions — read version config files (package.json engines, .csproj, go.mod, etc.).
        - Do NOT assume the error is in a specific file (e.g., package.json) without reading the error log.
        - Do NOT refactor code unrelated to the errors.
        - Do NOT change file paths, folder structure, or entry points unless the errors explicitly require it.
        - Do NOT invent credentials or secrets; use environment variables.
        - Do NOT stop fixing after the first error — you MUST fix ALL errors.
        - Do NOT submit incomplete fixes — the build must pass with zero remaining errors.
        - Do NOT run the build once and assume success without checking the exit code and output.
        - Do NOT call submit_deployment_files until the build explicitly reports success.
        - Do NOT call submit_deployment_files multiple times.
        - Do NOT return JSON in markdown fences.
        - Do NOT ignore cascading errors — if the first fix causes a new error, fix that too.
        - Do NOT skip infrastructure errors — document them clearly even if they require manual steps.

        ## Output Format

        When all errors are fixed and the build succeeds, call submit_deployment_files exactly once with all changed files:

        { "files": [{ "path": "relative/path/from/repo/root", "content": "full file contents" }] }

        Do NOT return JSON as plain text or markdown fences.
        """;

    internal static string BuildFixPrompt(
        string owner,
        string repo,
        string gitRef,
        string providerName,
        string? framework,
        DeploymentFailureAnalysis failureAnalysis,
        string? repoContext = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a developer fixing failed builds and resolving build errors in source code and configuration.");
        builder.AppendLine();
        builder.AppendLine("## Context");
        builder.AppendLine();
        builder.AppendLine($"- Repository: {owner}/{repo} @ {gitRef}");
        builder.AppendLine($"- Failed provider: {providerName}");
        if (!string.IsNullOrWhiteSpace(framework))
        {
            builder.AppendLine($"- Framework: {framework}");
        }

        builder.AppendLine($"- Failure type: {FormatFailureCategory(failureAnalysis.Category)}");
        if (failureAnalysis.ErrorCount > 1)
        {
            builder.AppendLine($"- Distinct errors detected: {failureAnalysis.ErrorCount}");
        }

        builder.AppendLine();
        builder.AppendLine("## Build Log");
        builder.AppendLine();
        builder.AppendLine(failureAnalysis.ErrorExcerpt ?? failureAnalysis.Summary);

        if (failureAnalysis.ReferencedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Structured Errors");
            builder.AppendLine();
            builder.AppendLine("A parsed list of files referenced by the distinct errors:");
            builder.AppendLine("```json");
            builder.AppendLine(JsonSerializer.Serialize(
                new
                {
                    errorCount = failureAnalysis.ErrorCount,
                    referencedFiles = failureAnalysis.ReferencedFiles
                },
                new JsonSerializerOptions { WriteIndented = true }));
            builder.AppendLine("```");
        }

        var actionableFix = BuildErrorActionHints.BuildSection(failureAnalysis.ErrorExcerpt ?? failureAnalysis.Summary);
        if (!string.IsNullOrWhiteSpace(actionableFix))
        {
            builder.AppendLine();
            builder.AppendLine(actionableFix);
        }

        if (!string.IsNullOrWhiteSpace(repoContext))
        {
            builder.AppendLine();
            builder.AppendLine("## Preloaded Repository Context");
            builder.AppendLine();
            builder.AppendLine(repoContext);
        }

        builder.AppendLine();
        builder.Append(FixInstructions);

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
        builder.AppendLine("You are a deployment engineer completing deployment setup for a repository.");
        builder.AppendLine();

        builder.AppendLine("## Context");
        builder.AppendLine($"- Repository: {owner}/{repo} @ {gitRef}");
        builder.AppendLine("- Deployment plan provided in JSON below");
        builder.AppendLine();

        builder.AppendLine("## Deployment Plan");
        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(parts, PlanSerializerOptions));
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Each part of the plan specifies:");
        builder.AppendLine("- Role: frontend, backend, or service identifier");
        builder.AppendLine("- ProviderName: hosting platform (Vercel, Railway, AWS, etc.)");
        builder.AppendLine("- Framework: technology stack (Angular, React, .NET, Node.js, Python, etc.)");
        builder.AppendLine("- RootDirectory: working directory for this part (relative to repo root)");
        builder.AppendLine("- ServiceDirectory: directory containing the service source code");
        builder.AppendLine("- BuildCommand: command to build the artifact");
        builder.AppendLine("- InstallCommand: command to install dependencies");
        builder.AppendLine("- StartCommand: command to start the service");
        builder.AppendLine("- OutputDirectory: where build output lands (or null)");
        builder.AppendLine("- DockerfilePath: path to Dockerfile (or null if not containerized)");
        builder.AppendLine();

        builder.AppendLine("## Architecture Notes");
        AppendArchitectureDescription(builder, parts);
        builder.AppendLine();

        builder.AppendLine("## Deployment Gaps (from readiness scan)");
        foreach (var missing in missingFiles)
        {
            builder.AppendLine($"- [{missing.Severity}] {missing.Path}: {missing.Reason}");
        }
        builder.AppendLine();

        builder.AppendLine("## Task");
        builder.AppendLine("1. Read only what you need: Use GitHub tools to inspect key files mentioned in the plan and scan gaps (~10–15 calls max).");
        builder.AppendLine("2. Understand existing structure: Read build configs, entry points, and framework-specific files to match conventions.");
        builder.AppendLine("3. Address every gap: For each gap, determine what file is needed and its purpose.");
        builder.AppendLine("4. Generate missing files: Create or update files that the deployment plan requires.");
        builder.AppendLine("5. Follow provider conventions: Use provider-native config files (vercel.json for Vercel, railway.toml for Railway, etc.).");
        builder.AppendLine("6. Use environment variables: Never hardcode secrets, API URLs, or host names — always use env vars.");
        builder.AppendLine("7. Preserve existing code: When modifying source files, read them first and make minimal, surgical edits.");
        builder.AppendLine("8. Submit all files: Call submit_deployment_files once with complete contents for every file created or modified.");
        builder.AppendLine();

        builder.AppendLine("## Repository Tools");
        builder.AppendLine("- github_list_directory: list files and folders at a path");
        builder.AppendLine("- github_read_file: read a file's full contents");
        builder.AppendLine("- submit_deployment_files: submit all generated/modified files (call once, then stop)");
        builder.AppendLine();

        builder.AppendLine("## Rules");
        builder.AppendLine();
        builder.AppendLine("### General");
        builder.AppendLine("- Do NOT write prose or plain-text JSON — submit files through the tool only.");
        builder.AppendLine("- Use paths relative to the repository root.");
        builder.AppendLine("- Align commands, directories, and framework defaults with the deployment plan.");
        builder.AppendLine("- Match existing namespaces, project names, class names, and coding conventions.");
        builder.AppendLine("- Return complete file contents, not diffs.");
        builder.AppendLine();

        builder.AppendLine("### Reading Existing Files");
        builder.AppendLine("- You MUST read a file before modifying it.");
        builder.AppendLine("- If a file does not exist at the expected path and is required, create it from scratch.");
        builder.AppendLine("- If a file does not exist and is optional, skip it.");
        builder.AppendLine("- If a file's structure differs from what you expect, adapt your changes to match the existing code, not the template.");
        builder.AppendLine();

        builder.AppendLine("### Configuration Files");
        builder.AppendLine("- Keep valid JSON, YAML, or TOML syntax.");
        builder.AppendLine("- Preserve all sibling keys (do not delete unrelated config).");
        builder.AppendLine("- Make the smallest edit needed to support the deployment.");
        builder.AppendLine("- Use provider-native formats (no custom config formats unless required by the plan).");
        builder.AppendLine();

        builder.AppendLine("### Environment Variables");
        builder.AppendLine("- Document all required env vars in a deployment guide or config schema.");
        builder.AppendLine("- Use standard naming: {SERVICE_NAME}_{PROPERTY} (e.g., DATABASE_URL, API_BASE_URL).");
        builder.AppendLine("- Inject env vars at build time or runtime, depending on the framework and provider.");
        builder.AppendLine("- Never embed secrets or service URLs in code.");
        builder.AppendLine();

        builder.AppendLine("### Cross-Service Communication");
        builder.AppendLine("- Read the architecture description to understand how services interact.");
        builder.AppendLine("- If a frontend calls an API backend, use an environment variable to configure the backend URL.");
        builder.AppendLine("- If a backend calls another service, use environment variables for service discovery.");
        builder.AppendLine("- Document required firewall rules, network policies, or CORS settings.");
        builder.AppendLine();

        builder.AppendLine("### Health Checks & Diagnostics");
        builder.AppendLine("- Expose a health check endpoint (if the role is a service).");
        builder.AppendLine("- Use a standard path (e.g., /health, /api/health, /api/v1/health).");
        builder.AppendLine("- Return a 2xx status code and minimal JSON on success.");
        builder.AppendLine("- Document the health check path in a deployment guide.");
        builder.AppendLine();

        builder.AppendLine("### Secrets & Credentials");
        builder.AppendLine("- Never hardcode API keys, tokens, or database credentials.");
        builder.AppendLine("- Use provider-native secrets management (GitHub Secrets, Railway Variables, Vercel Environment, etc.).");
        builder.AppendLine("- Document which secrets are required in a deployment guide.");
        builder.AppendLine();

        builder.AppendLine("## Output Format");
        builder.AppendLine("When you are done exploring and generating files, call submit_deployment_files exactly once with:");
        builder.AppendLine("```json");
        builder.AppendLine("{");
        builder.AppendLine("  \"files\": [");
        builder.AppendLine("    {");
        builder.AppendLine("      \"path\": \"relative/path/from/repo/root\",");
        builder.AppendLine("      \"content\": \"full file contents as a string\"");
        builder.AppendLine("    }");
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        builder.AppendLine("```");
        builder.AppendLine("Do NOT return JSON as plain text or markdown fences. The tool will validate syntax and file paths.");
        builder.AppendLine();

        builder.AppendLine("## Build Validation Step");
        builder.AppendLine("After submit_deployment_files returns successfully:");
        builder.AppendLine("- For each part in the deployment plan:");
        builder.AppendLine("  - Change to {RootDirectory}");
        builder.AppendLine("  - Run {InstallCommand} to install dependencies");
        builder.AppendLine("  - Run {BuildCommand} to build the artifact");
        builder.AppendLine("  - Verify the build succeeds (exit code 0)");
        builder.AppendLine("  - Verify output lands in {OutputDirectory} (if specified)");
        builder.AppendLine("- Validate generated config files:");
        builder.AppendLine("  - JSON files: parse and verify no syntax errors");
        builder.AppendLine("  - YAML/TOML files: validate format");
        builder.AppendLine("  - Shell scripts: verify shebang and permissions");
        builder.AppendLine("  - Dockerfile: verify FROM, RUN, ENTRYPOINT/CMD are present and valid");
        builder.AppendLine("- Check for common issues:");
        builder.AppendLine("  - Missing env var definitions in code or config");
        builder.AppendLine("  - Broken imports or module paths");
        builder.AppendLine("  - Invalid health check endpoints");
        builder.AppendLine("  - Mismatched start commands vs. framework conventions");
        builder.AppendLine("  - Hardcoded URLs or secrets");
        builder.AppendLine("- Report results:");
        builder.AppendLine("  - List all builds that succeeded");
        builder.AppendLine("  - If any build fails, show the error and suggest fixes");
        builder.AppendLine("  - If config validation fails, report the specific issue");
        builder.AppendLine("  - If common issues are detected, warn the user before deployment");
        builder.AppendLine("- Stop on first critical failure:");
        builder.AppendLine("  - If a build fails, do not proceed");
        builder.AppendLine("  - Do not attempt to build subsequent parts until the failure is resolved");
        builder.AppendLine("  - Provide clear error messages and context");
        builder.AppendLine();

        builder.AppendLine("## Workflow Example");
        builder.AppendLine("1. Read the deployment plan (JSON already provided).");
        builder.AppendLine("2. For each gap listed:");
        builder.AppendLine("   - Determine the file path and purpose.");
        builder.AppendLine("   - Check if the file exists using github_read_file.");
        builder.AppendLine("   - If it exists, read it and understand its structure.");
        builder.AppendLine("   - Create or update the file to address the gap.");
        builder.AppendLine("3. For provider config (vercel.json, railway.toml, etc.):");
        builder.AppendLine("   - Read if it exists to preserve sibling keys.");
        builder.AppendLine("   - Add or update only the keys needed for deployment.");
        builder.AppendLine("4. For source code (Program.cs, main.ts, app.py, etc.):");
        builder.AppendLine("   - Read the entire file.");
        builder.AppendLine("   - Make minimal edits (e.g., add a middleware, register a service).");
        builder.AppendLine("   - Return the complete updated file.");
        builder.AppendLine("5. When all gaps are addressed, submit all files in one call.");
        builder.AppendLine("6. Run build validation: Execute install and build commands for each part, validate configs, and report results.");
        builder.AppendLine();

        builder.AppendLine("## What NOT to Do");
        builder.AppendLine("- Do NOT make assumptions about directory structure; read the plan and existing files.");
        builder.AppendLine("- Do NOT ignore provider conventions (e.g., use railway.toml for Railway, not a custom config).");
        builder.AppendLine("- Do NOT hardcode URLs, API keys, or service names.");
        builder.AppendLine("- Do NOT modify code you haven't read first.");
        builder.AppendLine("- Do NOT return JSON or code in markdown fences.");
        builder.AppendLine("- Do NOT call submit_deployment_files multiple times.");

        return builder.ToString();
    }

    private static readonly JsonSerializerOptions PlanSerializerOptions = new()
    {
        WriteIndented = true
    };

    private static void AppendArchitectureDescription(StringBuilder builder, IReadOnlyList<DeploymentPlanPart> parts)
    {
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
    }

    private static string FormatFailureCategory(DeploymentFailureCategory category) =>
        category switch
        {
            DeploymentFailureCategory.CodeBuild => "codeError",
            DeploymentFailureCategory.Infrastructure => "infrastructureError",
            DeploymentFailureCategory.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled failure category.")
        };
}
