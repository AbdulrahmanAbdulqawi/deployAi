using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployAI.Core.Exceptions;
using DeployAI.Core.Providers;
using Microsoft.Extensions.Logging;

namespace DeployAI.Providers.Coolify;

public sealed partial class CoolifyProvider : IProviderApplicationConfigSync
{
    public async Task UpdateApplicationConfigAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpdateProviderApplicationConfigRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var buildPack = CoolifyApiSupport.ResolveBuildPack(
            request.CoolifyBuildPack,
            request.DockerfilePath,
            request.Framework,
            request.OutputDirectory,
            request.BuildCommand);

        var body = new Dictionary<string, object?>
        {
            ["build_pack"] = buildPack,
            ["ports_exposes"] = CoolifyApiSupport.ResolveExposedPort(buildPack, exposedPort: null, request.Framework)

            // custom_labels is deliberately not sent. It used to be set to a base64 empty array to
            // force Coolify to regenerate the Traefik labels it caches at first deploy, because a
            // build pack or port change otherwise had no effect on the live proxy. That cure was
            // worse: the proxy runs with --providers.docker.exposedbydefault=false, so a container
            // is routed only if it carries traefik.enable=true and its router labels, and writing an
            // empty set leaves it with none. Two applications deployed through this method were
            // reachable by nothing -- correct domain, correct port, container running, and Traefik
            // answering its own 404 -- while an application deployed before this method existed
            // still routed. Restarting and redeploying could not recover it, because the empty set
            // was reapplied every time.
            //
            // Leaving the field alone keeps Coolify's own label management, which every working
            // application on that instance already relies on. The stale-label problem this was
            // meant to solve is recorded in CLAUDE.md rather than traded for an unroutable app.
        };

        if (!string.IsNullOrWhiteSpace(request.RootDirectory))
        {
            body["base_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.RootDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            body["publish_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.OutputDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.BuildCommand))
        {
            body["build_command"] = request.BuildCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.InstallCommand))
        {
            body["install_command"] = request.InstallCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.StartCommand))
        {
            body["start_command"] = request.StartCommand;
        }

        if (!string.IsNullOrWhiteSpace(request.DockerfilePath))
        {
            body["dockerfile_location"] = request.DockerfilePath;
        }

        using var patchRequest = CreateRequest(HttpMethod.Patch, session, $"applications/{providerProjectId}");
        patchRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not update Coolify application config ({(int)response.StatusCode}).");
        }
    }

    public async Task<ProviderProject> CreateProjectAsync(
        ProviderCredentials credentials,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var projectUuid = await ResolveProjectUuidAsync(session, request, cancellationToken);
        var serverUuid = await ResolveServerUuidAsync(session, request, cancellationToken);
        var environment = await ResolveEnvironmentAsync(session, projectUuid, request, cancellationToken);

        // Creating is not idempotent on Coolify: the same name and repository can be created over
        // and over, each time as a separate application with its own domain. Re-running the wizard,
        // or retrying after a failure part-way through, would leave copies behind — so an existing
        // application for this name and repository is reused instead.
        var existing = await FindExistingApplicationAsync(session, request, environment, cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "Reusing existing Coolify application {Uuid} for {Name}; creating another would duplicate it.",
                existing.Id,
                request.Name);
            return existing;
        }

        var buildPack = CoolifyApiSupport.ResolveBuildPack(request);
        var gitRepository = CoolifyApiSupport.NormalizeGitHubRepoUrl(request.GitHubRepoFullName);
        var gitBranch = string.IsNullOrWhiteSpace(request.GitBranch) ? "main" : request.GitBranch.Trim();

        var isCompose = CoolifyApiSupport.IsComposeBuildPack(buildPack);

        var body = new Dictionary<string, object?>
        {
            ["project_uuid"] = projectUuid,
            ["server_uuid"] = serverUuid,
            ["environment_name"] = environment.Name,
            ["environment_uuid"] = environment.Uuid,
            ["git_repository"] = gitRepository,
            ["git_branch"] = gitBranch,
            ["build_pack"] = buildPack,
            ["name"] = request.Name.Trim(),
            // Deliberately deferred: the domain must be attached and read back before env vars
            // are written, because values like the public site URL *are* the final domain.
            ["instant_deploy"] = false
        };

        var exposedPort = CoolifyApiSupport.ResolveExposedPort(buildPack, request);
        if (exposedPort is not null)
        {
            body["ports_exposes"] = exposedPort;
        }

        // A caller-supplied domain replaces the autogenerated sslip.io hostname. Asking for
        // both makes Coolify generate one and then immediately overwrite it.
        if (string.IsNullOrWhiteSpace(request.CustomDomain))
        {
            body["autogenerate_domain"] = true;
        }

        if (isCompose)
        {
            body["docker_compose_location"] = NormalizeComposeLocation(request.ComposeFileLocation);
            // docker_compose_raw is not accepted on create ("This field is not allowed") — only
            // on the update. The domain PATCH below supplies it so per-service domains can be set
            // before the first deploy has had a chance to populate it.
        }

        // Follows the project's own setting rather than being forced on. Hardcoding it to true
        // both overrode a user who had deliberately turned auto-deploy off, and made every publish
        // build the same commit twice: DeployAI writes generated files (Dockerfile, compose) into
        // the repository mid-publish, and Coolify's webhook then started a build for that commit
        // alongside the deploy DeployAI was already running for it. One observed publish produced
        // three concurrent builds of one website -- 6m19s, 11m10s and 11m17s -- for a Next.js app
        // that compiles in 16 seconds, because the duplicates contended for the same machine.
        body["is_auto_deploy_enabled"] = request.AutoDeployEnabled;

        if (!string.IsNullOrWhiteSpace(request.RootDirectory) && !isCompose)
        {
            // Rooted at the repository, like docker_compose_location: a bare relative path is
            // rejected with "The base directory field format is invalid".
            body["base_directory"] = $"/{request.RootDirectory.Trim().Replace('\\', '/').TrimStart('/')}";
        }

        // publish_directory / is_static only apply to the static build pack. An SSR framework
        // (Next, Nuxt…) also reports an output directory (.next), but it builds with Nixpacks
        // and runs a Node server — sending publish_directory then fails Coolify validation
        // ("publish directory field format is invalid") and would be wrong even if accepted.
        if (!string.IsNullOrWhiteSpace(request.OutputDirectory) &&
            string.Equals(buildPack, CoolifyBuildPackValues.Static, StringComparison.OrdinalIgnoreCase))
        {
            body["publish_directory"] = CoolifyApiSupport.NormalizeDirectoryPath(request.OutputDirectory);
            body["is_static"] = true;
        }

        if (!isCompose)
        {
            if (!string.IsNullOrWhiteSpace(request.BuildCommand))
            {
                body["build_command"] = request.BuildCommand;
            }

            if (!string.IsNullOrWhiteSpace(request.InstallCommand))
            {
                body["install_command"] = request.InstallCommand;
            }

            if (!string.IsNullOrWhiteSpace(request.StartCommand))
            {
                body["start_command"] = request.StartCommand;
            }

            if (!string.IsNullOrWhiteSpace(request.DockerfilePath))
            {
                body["dockerfile_location"] = request.DockerfilePath;
            }
        }

        // Compose apps are created through the same git-backed endpoints as any other; the
        // build pack is what makes them compose. Coolify has no /applications/dockercompose
        // route — verified against a live v4.1.2 instance, which 404s it while answering the
        // routes below.
        string createPath;
        if (request.IsPrivateRepository)
        {
            var githubAppUuid = await ResolveGithubAppUuidAsync(session, request, cancellationToken);
            body["github_app_uuid"] = githubAppUuid;
            createPath = "applications/private-github-app";
        }
        else
        {
            createPath = "applications/public";
        }

        using var createRequest = CreateRequest(HttpMethod.Post, session, createPath);
        createRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(createRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create Coolify application ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyCreateApplicationResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Coolify returned an empty application response.");

        if (string.IsNullOrWhiteSpace(created.Uuid))
        {
            throw new DeployAIException(
                "coolify_api_error",
                "Coolify did not return an application id.");
        }

        // A compose app's domain can only be attached after its first deploy has populated
        // docker_compose_raw — Coolify rejects docker_compose_domains before then, and the raw
        // field is not settable through the API. So the domain is deferred to a post-deploy step
        // (see AssignComposeDomainAsync). A single-app (fqdn) domain has no such dependency.
        if (!string.IsNullOrWhiteSpace(request.CustomDomain) && !isCompose)
        {
            await AssignDomainAsync(session, created.Uuid, request.CustomDomain!, request.DomainServiceName, isCompose: false, cancellationToken);
        }

        var application = await TryGetApplicationAsync(session, created.Uuid, cancellationToken);
        return new ProviderProject(
            created.Uuid,
            application?.Name ?? request.Name.Trim(),
            NormalizeUrl(application?.Fqdn),
            gitBranch);
    }

    /// <summary>
    /// Looks for an application that already represents this request — same name, same repository,
    /// same environment — so a repeated create reuses it rather than adding another copy. Matching
    /// on the environment as well as the name keeps deliberately separate staging and production
    /// apps distinct; they share a name but not an environment.
    /// </summary>
    private async Task<ProviderProject?> FindExistingApplicationAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CoolifyEnvironmentOption environment,
        CancellationToken cancellationToken)
    {
        using var listRequest = CreateRequest(HttpMethod.Get, session, "applications");
        var response = await _httpClient.SendAsync(listRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Can't tell either way — fall through to create rather than block the deploy.
            return null;
        }

        var applications = await response.Content.ReadFromJsonAsync<List<CoolifyApplicationSummary>>(cancellationToken) ?? [];
        var desiredName = request.Name.Trim();
        var desiredRepository = CoolifyApiSupport.NormalizeGitHubRepoUrl(request.GitHubRepoFullName);

        var match = applications.FirstOrDefault(app =>
            !string.IsNullOrWhiteSpace(app.Uuid) &&
            string.Equals(app.Name, desiredName, StringComparison.OrdinalIgnoreCase) &&
            RepositoryMatches(app.GitRepository, desiredRepository) &&
            (app.EnvironmentId is null || environment.Id is null || app.EnvironmentId == environment.Id));

        if (match is null)
        {
            return null;
        }

        return new ProviderProject(
            match.Uuid!,
            match.Name ?? desiredName,
            NormalizeUrl(match.Fqdn),
            string.IsNullOrWhiteSpace(match.GitBranch) ? request.GitBranch : match.GitBranch);
    }

    /// <summary>Coolify may store the remote with or without a .git suffix or scheme variations.</summary>
    private static bool RepositoryMatches(string? actual, string? desired)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(desired))
        {
            return false;
        }

        static string Strip(string value) => value
            .Trim()
            .TrimEnd('/')
            .Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("git@github.com:", "https://github.com/", StringComparison.OrdinalIgnoreCase);

        return string.Equals(Strip(actual), Strip(desired), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attaches a compose app's domain to its browser-facing service, after the first deploy has
    /// populated docker_compose_raw. Call this post-deploy, then redeploy for the route to take
    /// effect. Attaching it earlier fails — Coolify won't accept per-service domains until the
    /// compose file has been parsed, and there is no API to set the raw compose directly.
    /// </summary>
    public async Task AssignComposeDomainAsync(
        ProviderCredentials credentials,
        string applicationUuid,
        string domain,
        string? serviceName,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        await AssignDomainAsync(session, applicationUuid, domain, serviceName, isCompose: true, cancellationToken);
    }

    /// <summary>
    /// Moves an existing application onto the Dockerfile build pack. Needed because Nixpacks
    /// cannot be given the app's environment at build time, so a framework that inlines values
    /// into its bundle builds the wrong configuration; switching an already-created app avoids
    /// recreating it and losing its domain. Returns false when Coolify rejects the change, so the
    /// caller can leave the existing build pack alone rather than deploy a half-configured app.
    /// </summary>
    public async Task<bool> ConfigureDockerfileBuildAsync(
        ProviderCredentials credentials,
        string applicationUuid,
        string baseDirectory,
        string dockerfileLocation,
        string? exposedPort,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var body = new Dictionary<string, object?>
        {
            ["build_pack"] = CoolifyBuildPackValues.Dockerfile,
            // Both are rooted at the repository with a leading slash; a bare relative path is
            // rejected with "The base directory field format is invalid".
            ["base_directory"] = $"/{baseDirectory.Trim().Replace('\\', '/').Trim('/')}",
            ["dockerfile_location"] = $"/{dockerfileLocation.Trim().Replace('\\', '/').TrimStart('/')}"
        };

        if (!string.IsNullOrWhiteSpace(exposedPort))
        {
            body["ports_exposes"] = exposedPort.Trim();
        }

        using var request = CreateRequest(HttpMethod.Patch, session, $"applications/{applicationUuid}");
        request.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "Could not switch Coolify application {Uuid} to the Dockerfile build pack ({Status}): {Body}",
            applicationUuid,
            (int)response.StatusCode,
            responseBody[..Math.Min(300, responseBody.Length)]);
        return false;
    }

    /// <summary>
    /// Attaches the domain to the application, and for compose to a specific service. Without
    /// this, Traefik has no rule to route on and no certificate is issued — the deploy succeeds
    /// and the site is unreachable.
    /// </summary>
    private async Task AssignDomainAsync(
        CoolifyApiSupport.CoolifySession session,
        string applicationUuid,
        string customDomain,
        string? domainServiceName,
        bool isCompose,
        CancellationToken cancellationToken)
    {
        var domain = NormalizeUrl(customDomain)!;
        var body = new Dictionary<string, object?>();

        if (isCompose)
        {
            // Only docker_compose_domains: Coolify rejects a compose app that also carries a
            // top-level `domains`, because a compose resource routes per service rather than
            // as a whole.
            var serviceName = string.IsNullOrWhiteSpace(domainServiceName)
                ? "web"
                : domainServiceName.Trim();

            // Compose apps route per service: the domain belongs to the one service that faces
            // the browser, and everything else stays on the internal network. Coolify wants the
            // service named inside the entry as well as keyed by it — omitting `name` is
            // rejected with "docker_compose_domains.<service>.name field is required".
            body["docker_compose_domains"] = new Dictionary<string, object?>
            {
                [serviceName] = new { name = serviceName, domain }
            };
        }
        else
        {
            body["domains"] = domain;
            body["fqdn"] = domain;
        }

        using var patchRequest = CreateRequest(HttpMethod.Patch, session, $"applications/{applicationUuid}");
        patchRequest.Content = JsonContent.Create(body);
        var response = await _httpClient.SendAsync(patchRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_domain_failed",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not attach {domain} to the Coolify application ({(int)response.StatusCode}).");
        }
    }

    /// <summary>
    /// Coolify expects this rooted at the repository with a leading slash, and rejects a bare
    /// relative path with "The docker compose location field format is invalid" — verified
    /// against a live v4.1.2 instance.
    /// </summary>
    private static string NormalizeComposeLocation(string? location)
    {
        var trimmed = location?.Trim().Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(trimmed) ? "/docker-compose.yml" : $"/{trimmed}";
    }

    public async Task<IReadOnlyList<ProviderEnvVar>> ListEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var envs = await ListRawEnvVarsAsync(session, providerProjectId, cancellationToken);
        return envs
            .Where(env => !string.IsNullOrWhiteSpace(env.Key))
            .Select(env => new ProviderEnvVar(
                env.Uuid ?? env.Key!,
                env.Key!,
                env.IsShownOnce == true ? null : env.Value,
                "plain",
                [],
                env.IsShownOnce == true))
            .OrderBy(env => env.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fetches the application's variables exactly as Coolify returns them: one entry per record,
    /// in Coolify's own order, duplicates included. <see cref="ListEnvVarsAsync"/> reshapes and
    /// re-sorts for callers; reconciliation needs the raw order, because which record Coolify's
    /// bulk upsert writes to depends on it.
    /// </summary>
    private async Task<IReadOnlyList<CoolifyEnvironmentVariable>> ListRawEnvVarsAsync(
        CoolifyApiSupport.CoolifySession session,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{providerProjectId}/envs");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not list Coolify environment variables ({(int)response.StatusCode}).");
        }

        return JsonSerializer.Deserialize<List<CoolifyEnvironmentVariable>>(responseBody) ?? [];
    }

    /// <summary>
    /// Removes duplicate variable records from an application, keeping the first record for each
    /// key, and returns how many were deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="UpsertEnvVarsAsync"/> stops new duplicates being created, but it cannot repair
    /// an application that already has them, and on such an application it makes the situation
    /// actively misleading: Coolify's bulk handler resolves a key with
    /// <c>-&gt;where('key', $key)-&gt;first()</c>, so it updates the first record and leaves every
    /// later one untouched. Those later records keep whatever value they were created with and
    /// silently stop tracking reality, while which one the container actually receives is not
    /// something DeployAI controls.
    /// </para>
    /// <para>
    /// Keeping the first record therefore is not a preference — it is the only choice consistent
    /// with where subsequent writes land. One live application was found holding 32 records for 16
    /// keys, and every stale Postgres copy pointed at a database that no longer existed on the
    /// instance at all.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileDuplicateEnvVarsAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        return await ReconcileDuplicateEnvVarsAsync(session, providerProjectId, cancellationToken);
    }

    private async Task<int> ReconcileDuplicateEnvVarsAsync(
        CoolifyApiSupport.CoolifySession session,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var envs = await ListRawEnvVarsAsync(session, providerProjectId, cancellationToken);

        // Ordinal grouping: Coolify stores variables in Postgres, where `where('key', $key)` is
        // case-sensitive, so KEY and Key are two distinct records and neither shadows the other.
        // Grouping case-insensitively here would delete a record Coolify still resolves.
        var stale = envs
            .Where(env => !string.IsNullOrWhiteSpace(env.Key) && !string.IsNullOrWhiteSpace(env.Uuid))
            .GroupBy(env => env.Key!, StringComparer.Ordinal)
            .SelectMany(group => group.Skip(1))
            .ToList();

        foreach (var duplicate in stale)
        {
            await DeleteEnvVarAsync(session, providerProjectId, duplicate.Uuid!, cancellationToken);
        }

        return stale.Count;
    }

    public async Task<ProviderEnvVar> UpsertEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        UpsertProviderEnvVarRequest request,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);

        // Coolify's bulk endpoint resolves each entry by key server-side, updating when the key
        // exists and creating when it does not, in a single request.
        //
        // This used to list the app's env vars and then POST or PATCH based on what it saw. That
        // read-then-write was not atomic: two syncs running against the same application could
        // both observe a key as absent and both POST it, and Coolify's create endpoint does not
        // dedupe by key. The result was applications carrying two records per key -- including
        // two DATABASE_URLs pointing at different Postgres instances, where which one reached the
        // container was left to chance. Going through /envs/bulk removes the client-side window
        // entirely; see UpsertEnvVarsAsync for the batched form.
        var applied = await UpsertEnvVarsAsync(
            session,
            providerProjectId,
            [request],
            cancellationToken);

        var match = applied.FirstOrDefault(env =>
            string.Equals(env.Key, request.Key, StringComparison.OrdinalIgnoreCase));

        return new ProviderEnvVar(
            match?.Uuid ?? request.Key,
            request.Key,
            null,
            request.Type,
            request.Targets.ToList(),
            match?.IsShownOnce == true);
    }

    /// <summary>
    /// Applies every supplied variable in one <c>PATCH /envs/bulk</c> call. Prefer this over
    /// calling <see cref="UpsertEnvVarAsync"/> in a loop: it is one round trip instead of N, and
    /// it keeps the whole set inside a single server-side upsert rather than interleaving with a
    /// concurrent sync partway through.
    /// </summary>
    private async Task<IReadOnlyList<CoolifyEnvironmentVariable>> UpsertEnvVarsAsync(
        CoolifyApiSupport.CoolifySession session,
        string providerProjectId,
        IReadOnlyList<UpsertProviderEnvVarRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        using var bulkRequest = CreateRequest(
            HttpMethod.Patch,
            session,
            $"applications/{providerProjectId}/envs/bulk");
        // Every flag must be sent explicitly on every entry. Coolify's bulk handler reads them as
        // `$item->get('is_literal') ?? false` and then assigns unconditionally, so omitting a flag
        // does not leave it alone -- it writes false. Sending only key/value would silently clear
        // is_literal (Coolify then interpolates $ and {} in the value, corrupting generated
        // passwords and keys), is_shown_once (secrets become readable) and is_multiline on every
        // variable this touches. is_runtime/is_buildtime are guarded by `has()` on Coolify's side,
        // but are sent for the same reason: so the wire payload states intent rather than relying
        // on a default.
        bulkRequest.Content = JsonContent.Create(new
        {
            data = requests
                .Select(BuildEnvVarPayload)
                .ToArray()
        });

        var response = await _httpClient.SendAsync(bulkRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not set Coolify environment variables ({(int)response.StatusCode}).");
        }

        // Coolify documents a 201 carrying the updated variables, but the body is advisory here --
        // the write already succeeded. Treat an unexpected shape as "applied, uuid unknown" rather
        // than failing a request that went through.
        try
        {
            return JsonSerializer.Deserialize<List<CoolifyEnvironmentVariable>>(responseBody) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The Type and Targets on the request used to be accepted and silently dropped, so a
    /// secret landed in Coolify as a readable plain value and a build-time variable was never
    /// available to the image build.
    /// </summary>
    private static Dictionary<string, object?> BuildEnvVarPayload(UpsertProviderEnvVarRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["key"] = request.Key,
            ["value"] = request.Value,
            // Coolify interpolates unescaped values; literal keeps $ and {} intact, which
            // matters for generated keys and passwords. Both this and is_multiline are stated on
            // every payload rather than only when true: Coolify's bulk handler assigns them from
            // `?? false`, so an omitted flag is written as false rather than left alone.
            ["is_literal"] = true,
            ["is_multiline"] = request.Value.Contains('\n'),
            ["is_shown_once"] = ProviderEnvVarTypes.IsSecret(request.Type)
        };

        if (ProviderEnvVarTypes.IsBuildTime(request.Type))
        {
            // is_buildtime, not is_build_time: the latter is not a field Coolify accepts. The
            // single-env endpoint rejects unknown fields outright and the bulk endpoint drops them
            // in `only()`, so the misspelling meant build-time was never actually marked. Left
            // conditional because Coolify defaults it to true on create and guards it with `has()`
            // on update -- sending false for runtime-only variables would take away build access
            // they currently have.
            payload["is_buildtime"] = true;
        }

        if (request.Targets.Any(target => string.Equals(target, "preview", StringComparison.OrdinalIgnoreCase)))
        {
            payload["is_preview"] = true;
        }

        return payload;
    }

    public async Task DeleteEnvVarAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        await DeleteEnvVarAsync(session, providerProjectId, envVarId, cancellationToken);
    }

    private async Task DeleteEnvVarAsync(
        CoolifyApiSupport.CoolifySession session,
        string providerProjectId,
        string envVarId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            session,
            $"applications/{providerProjectId}/envs/{envVarId}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not delete Coolify environment variable ({(int)response.StatusCode}).");
        }
    }

    public async Task DeleteProjectAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        using var request = CreateRequest(
            HttpMethod.Delete,
            session,
            $"applications/{providerProjectId}{CoolifyApiSupport.ResourceCleanupQuery}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Already gone is the outcome we wanted. Treating it as an error makes a retried teardown
        // fail forever on the one resource that was actually cleaned up first time.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not delete Coolify application ({(int)response.StatusCode}).");
        }
    }

    /// <summary>Lists the projects, servers, and GitHub Apps visible on a Coolify connection, for the setup UI.</summary>
    public async Task<CoolifyInfrastructureSnapshot> ListInfrastructureAsync(
        ProviderCredentials credentials,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        var servers = await ListCoolifyServersAsync(session, cancellationToken);
        var githubApps = await ListCoolifyGithubAppsAsync(session, cancellationToken);
        return new CoolifyInfrastructureSnapshot(
            projects.Select(MapInfrastructureResource).ToList(),
            servers.Select(MapInfrastructureResource).ToList(),
            githubApps.Select(MapInfrastructureResource).ToList());
    }

    /// <summary>Lists the environments (e.g. production, staging) within a Coolify project.</summary>
    public async Task<IReadOnlyList<CoolifyInfrastructureResource>> ListProjectEnvironmentsAsync(
        ProviderCredentials credentials,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        var session = CoolifyApiSupport.ParseSession(credentials);
        var environments = await ListCoolifyEnvironmentsAsync(session, projectUuid, cancellationToken);
        return environments
            .Select(env => new CoolifyInfrastructureResource(env.Uuid, env.Name))
            .ToList();
    }

    /// <summary>Resolves which Coolify project to create the application in: an explicit uuid if given, else the first existing project, else a newly created one.</summary>
    private async Task<string> ResolveProjectUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyProjectUuid))
        {
            return request.CoolifyProjectUuid;
        }

        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        if (projects.Count == 1)
        {
            return projects[0].Uuid;
        }

        // Taking [0] out of several was fine for a demo, wrong for the default production
        // target — it silently drops the app into whichever project Coolify happened to list
        // first. Make the caller choose instead.
        if (projects.Count > 1)
        {
            throw new DeployAIException(
                "coolify_project_ambiguous",
                "Your Coolify instance has more than one project. Pick which one to deploy into: " +
                string.Join(", ", projects.Select(project => project.Name)));
        }

        using var createRequest = CreateRequest(HttpMethod.Post, session, "projects");
        createRequest.Content = JsonContent.Create(new { name = request.Name.Trim() });
        var response = await _httpClient.SendAsync(createRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeployAIException(
                "coolify_api_error",
                CoolifyApiSupport.ParseErrorMessage(responseBody)
                    ?? $"Could not create Coolify project ({(int)response.StatusCode}).");
        }

        var created = await response.Content.ReadFromJsonAsync<CoolifyUuidResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Uuid))
        {
            throw new DeployAIException(
                "coolify_api_error",
                "Coolify did not return a project id.");
        }

        return created.Uuid;
    }

    /// <summary>Resolves which Coolify server to deploy to: an explicit uuid if given, else the first configured server. Throws if none exist.</summary>
    private async Task<string> ResolveServerUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyServerUuid))
        {
            return request.CoolifyServerUuid;
        }

        var servers = await ListCoolifyServersAsync(session, cancellationToken);
        if (servers.Count == 0)
        {
            throw new DeployAIException(
                "coolify_no_server",
                "No servers are configured in your Coolify instance. Add a server in Coolify first.");
        }

        if (servers.Count > 1)
        {
            throw new DeployAIException(
                "coolify_server_ambiguous",
                "Your Coolify instance has more than one server. Pick which one to deploy to: " +
                string.Join(", ", servers.Select(server => server.Name)));
        }

        return servers[0].Uuid;
    }

    /// <summary>Resolves which environment to deploy to: an explicit name/uuid if given and found, else "production" if it exists, else the first environment.</summary>
    private async Task<CoolifyEnvironmentOption> ResolveEnvironmentAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        var environments = await ListCoolifyEnvironmentsAsync(session, projectUuid, cancellationToken);
        if (environments.Count == 0)
        {
            // A Coolify project with no environments used to dead-end here, and the only way out
            // was for someone to open Coolify and add one by hand -- the exact manual step this
            // product exists to remove. It happens for real: deleting a project's last environment
            // leaves the project itself behind, and Coolify's dashboard stops listing it, so the
            // state is easy to reach and hard to see.
            return await CreateEnvironmentAsync(
                session,
                projectUuid,
                string.IsNullOrWhiteSpace(request.CoolifyEnvironmentName)
                    ? "production"
                    : request.CoolifyEnvironmentName.Trim(),
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.CoolifyEnvironmentName))
        {
            var match = environments.FirstOrDefault(env =>
                string.Equals(env.Name, request.CoolifyEnvironmentName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env.Uuid, request.CoolifyEnvironmentName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return environments.FirstOrDefault(env =>
                   string.Equals(env.Name, "production", StringComparison.OrdinalIgnoreCase))
               ?? environments[0];
    }

    /// <summary>Resolves the Coolify GitHub App for a private repo: an explicit uuid if given, else the sole configured app. Throws if none or more than one exist and none was chosen.</summary>
    private async Task<string> ResolveGithubAppUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CreateProviderProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.CoolifyGithubAppUuid))
        {
            return request.CoolifyGithubAppUuid;
        }

        var githubApps = await ListCoolifyGithubAppsAsync(session, cancellationToken);
        if (githubApps.Count == 0)
        {
            throw new DeployAIException(
                "coolify_no_github_app",
                "Private repositories require a GitHub App configured in Coolify. Set one up in Coolify → Sources → GitHub Apps.");
        }

        if (githubApps.Count > 1)
        {
            throw new DeployAIException(
                "coolify_github_app_required",
                "Choose which Coolify GitHub App to use for this private repository.");
        }

        return githubApps[0].Uuid;
    }

    private async Task<CoolifyApplication?> TryGetApplicationAsync(
        CoolifyApiSupport.CoolifySession session,
        string applicationUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"applications/{applicationUuid}");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CoolifyApplication>(cancellationToken);
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyProjectsAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "projects");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify projects.");
        var projects = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return projects.Where(project => !string.IsNullOrWhiteSpace(project.Uuid)).ToList();
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyServersAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "servers");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify servers.");
        var servers = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return servers.Where(server => !string.IsNullOrWhiteSpace(server.Uuid)).ToList();
    }

    private async Task<IReadOnlyList<CoolifyNamedResource>> ListCoolifyGithubAppsAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, "github-apps");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify GitHub apps.");
        var githubApps = await response.Content.ReadFromJsonAsync<List<CoolifyNamedResource>>(cancellationToken) ?? [];
        return githubApps.Where(app => !string.IsNullOrWhiteSpace(app.Uuid)).ToList();
    }

    /// <summary>
    /// Creates an environment in a Coolify project and returns it.
    /// </summary>
    /// <remarks>
    /// Coolify's create endpoint accepts <c>name</c> and nothing else -- any extra field is
    /// rejected with a 422 -- and answers with the uuid alone, so the name is carried over from
    /// the request rather than read back. A 409 means something created the same name in between
    /// the list and this call; that is the outcome we wanted, so re-read and use it instead of
    /// failing the deploy.
    /// </remarks>
    private async Task<CoolifyEnvironmentOption> CreateEnvironmentAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        string environmentName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            session,
            $"projects/{projectUuid}/environments");
        request.Content = JsonContent.Create(new { name = environmentName });

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await ListCoolifyEnvironmentsAsync(session, projectUuid, cancellationToken);
            var match = existing.FirstOrDefault(env =>
                string.Equals(env.Name, environmentName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        await EnsureSuccessAsync(response, cancellationToken, "Could not create a Coolify environment.");

        var created = await response.Content.ReadFromJsonAsync<CoolifyUuidResponse>(cancellationToken);
        if (string.IsNullOrWhiteSpace(created?.Uuid))
        {
            throw new DeployAIException(
                "coolify_api_error",
                "Coolify did not return an environment id.");
        }

        return new CoolifyEnvironmentOption { Uuid = created.Uuid, Name = environmentName };
    }

    private async Task<IReadOnlyList<CoolifyEnvironmentOption>> ListCoolifyEnvironmentsAsync(
        CoolifyApiSupport.CoolifySession session,
        string projectUuid,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, session, $"projects/{projectUuid}/environments");
        var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, "Could not list Coolify environments.");
        var environments = await response.Content.ReadFromJsonAsync<List<CoolifyEnvironmentOption>>(cancellationToken) ?? [];
        return environments
            .Where(env => !string.IsNullOrWhiteSpace(env.Uuid) && !string.IsNullOrWhiteSpace(env.Name))
            .ToList();
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        string fallbackMessage)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new DeployAIException(
            "coolify_api_error",
            CoolifyApiSupport.ParseErrorMessage(responseBody) ?? fallbackMessage);
    }

    private static CoolifyInfrastructureResource MapInfrastructureResource(CoolifyNamedResource resource) =>
        new(
            resource.Uuid,
            string.IsNullOrWhiteSpace(resource.Name) ? resource.Uuid : resource.Name);

    private sealed class CoolifyCreateApplicationResponse
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }
    }

    private sealed class CoolifyUuidResponse
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }
    }

    private sealed class CoolifyEnvironmentVariable
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("is_shown_once")]
        public bool? IsShownOnce { get; set; }
    }

    internal sealed class CoolifyNamedResource
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CoolifyApplicationSummary
    {
        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("git_repository")]
        public string? GitRepository { get; set; }

        [JsonPropertyName("git_branch")]
        public string? GitBranch { get; set; }

        [JsonPropertyName("fqdn")]
        public string? Fqdn { get; set; }

        [JsonPropertyName("environment_id")]
        public int? EnvironmentId { get; set; }
    }

    private sealed class CoolifyEnvironmentOption
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    // Coolify's GET /applications/{uuid} does not return project_uuid/server_uuid/environment_name
    // as flat fields — only a numeric environment_id and a destination. Resolving the app's
    // project + environment therefore means matching that environment_id against the environments
    // listed under each project.
    internal async Task<(string ProjectUuid, string EnvironmentName, string EnvironmentUuid)?>
        ResolveProjectEnvironmentByEnvironmentIdAsync(
            CoolifyApiSupport.CoolifySession session,
            int environmentId,
            CancellationToken cancellationToken)
    {
        var projects = await ListCoolifyProjectsAsync(session, cancellationToken);
        foreach (var project in projects)
        {
            using var request = CreateRequest(HttpMethod.Get, session, $"projects/{project.Uuid}/environments");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var environments = await response.Content.ReadFromJsonAsync<List<CoolifyEnvironmentOption>>(cancellationToken) ?? [];
            var match = environments.FirstOrDefault(env =>
                env.Id == environmentId && !string.IsNullOrWhiteSpace(env.Uuid) && !string.IsNullOrWhiteSpace(env.Name));
            if (match is not null)
            {
                return (project.Uuid, match.Name, match.Uuid);
            }
        }

        return null;
    }

    internal async Task<string?> ResolveSingleServerUuidAsync(
        CoolifyApiSupport.CoolifySession session,
        CancellationToken cancellationToken)
    {
        var servers = await ListCoolifyServersAsync(session, cancellationToken);
        return servers.Count == 1 ? servers[0].Uuid : null;
    }
}
