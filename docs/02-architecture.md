# DeployHub — Architecture

## 1. High-level overview

DeployHub is a three-tier web application with a background worker tier for long-running deployment jobs.

```
Angular 18 SPA  ─HTTP/WS─►  .NET 8 API  ─►  PostgreSQL
                                │
                                ├─► Hangfire workers ─► Provider APIs (Vercel, Railway, …)
                                └─► SignalR hub ─────► live logs back to SPA
```

### Tiers

1. **Presentation** — Angular 18 single-page app. Talks to the API over HTTPS and holds a SignalR connection for live logs.
2. **API** — ASP.NET Core. Handles auth, project CRUD, deploy triggering, and hosts the SignalR hub.
3. **Worker** — Hangfire background jobs execute deployments, poll provider status, and push logs into SignalR.
4. **Data** — PostgreSQL via EF Core.

## 2. Component responsibilities

| Component | Responsibility |
|-----------|----------------|
| Auth module | GitHub OAuth flow, JWT issuance, token refresh |
| Credential vault | Encrypt/decrypt provider tokens, validate against providers |
| Projects service | CRUD for projects and provider targets |
| Deployment orchestrator | Turn a deploy request into background jobs; provider-agnostic |
| Provider factory | Resolve a provider implementation by name |
| Provider implementations | Provider-specific API calls behind a shared interface |
| Log hub (SignalR) | Stream logs from workers to connected clients |
| Notification service | Send email (and later Slack) on completion |

## 3. The provider plugin design

This is the heart of the system. Everything provider-specific lives behind one interface, so the rest of the codebase never branches on "is this Vercel or Railway."

### 3.1 The contract

```csharp
public interface IDeploymentProvider
{
    string ProviderName { get; }

    Task<DeploymentResponse> TriggerDeploymentAsync(
        ProviderCredentials credentials,
        string providerProjectId,
        string branch,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken ct);

    Task<DeploymentStatus> GetStatusAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken ct);

    IAsyncEnumerable<string> StreamLogsAsync(
        ProviderCredentials credentials,
        string deploymentId,
        CancellationToken ct);

    Task<bool> ValidateCredentialsAsync(
        ProviderCredentials credentials,
        CancellationToken ct);

    Task<IReadOnlyList<ProviderProject>> ListProjectsAsync(
        ProviderCredentials credentials,
        CancellationToken ct);
}
```

Shared types (`DeploymentResponse`, `DeploymentStatus`, `ProviderProject`, `ProviderCredentials`) are defined once and used by every provider. Providers translate their own API shapes into these common types.

### 3.2 Why this works across different API styles

- **Vercel** is a REST API. Its provider implementation uses `HttpClient` and maps JSON responses to the shared types.
- **Railway** is a GraphQL API. Its implementation uses a GraphQL client and maps query results to the same shared types.

The orchestrator calling `provider.TriggerDeploymentAsync(...)` is identical in both cases. The differences are fully contained.

### 3.3 Provider factory and registry

```csharp
public interface IProviderFactory
{
    IDeploymentProvider GetProvider(string providerName);
    IEnumerable<string> GetAvailableProviders();
}
```

Providers are registered in one place during startup. The factory resolves the correct implementation from the DI container by name. The list of available providers is exposed to the frontend via an API endpoint, so the UI updates automatically when a provider is added.

### 3.4 Adding a new provider

1. Create `RenderProvider : IDeploymentProvider` and implement the five methods.
2. Register it: add one line in the provider registration extension and one entry in the factory map.
3. Done. Orchestrator, API, database, and frontend all pick it up with no further changes.

This is guaranteed by keeping the orchestrator, persistence, and API layers written only against `IDeploymentProvider` and the shared types — never against a concrete provider.

## 4. Deployment flow (happy path)

1. User clicks **Deploy** on a project in the SPA.
2. SPA calls `POST /api/projects/{id}/deployments`.
3. API creates a `Deployment` record (status: pending), enqueues a Hangfire job per provider target, and returns the deployment id immediately.
4. SPA opens/joins a SignalR group for that deployment id and shows the live log view.
5. Each worker job:
   - Decrypts the relevant provider credentials.
   - Resolves the provider via the factory.
   - Calls `TriggerDeploymentAsync`.
   - Polls `GetStatusAsync` and consumes `StreamLogsAsync`, pushing each log line into the SignalR group.
   - Updates the `Deployment` / provider-target status as it transitions.
6. On completion, the worker persists final status and logs, then triggers the notification service.
7. SPA shows final status; the user gets an email.

## 5. Real-time logs

- Workers push log lines to a SignalR hub keyed by deployment id.
- The SPA subscribes to the group for the deployment it's viewing.
- If WebSocket is unavailable, SignalR automatically falls back to server-sent events or long polling.
- All log lines are also written to the database so history views can replay them.

## 6. Security architecture

- **Credential encryption** — provider tokens and the GitHub token are encrypted with AES-256 before persistence, using a key held outside the database (environment/secret manager). EF Core value converters handle encrypt-on-write and decrypt-on-read.
- **Authorization** — every request carries a JWT; the API resolves the user and scopes all queries to that user's data. No cross-user access is possible because queries always filter by owner.
- **Webhook verification** — GitHub push webhooks (later phase) are verified via HMAC-SHA256 against the stored secret before processing.
- **Transport** — HTTPS enforced; HSTS in production.
- **Rate limiting** — per-user deploy rate limits and per-provider throttling in the worker queue protect against abuse and provider limits.

## 7. Technology choices and rationale

| Choice | Rationale |
|--------|-----------|
| Angular 18 | Matches the team's stack; standalone components + signals for a modern, clean SPA |
| .NET 8 | Team's core competency; excellent async, DI, and background-job story |
| PostgreSQL | Reliable, JSON columns fit flexible per-provider config |
| EF Core 8 | First-class migrations and value converters for encryption |
| Hangfire | Mature background jobs with retries, dashboard, and concurrency control |
| SignalR | Native .NET real-time with graceful transport fallback |
| GraphQL client (Railway) | Proves the abstraction holds across REST and GraphQL providers |

## 8. Deployment topology (of DeployHub itself)

- **Frontend** hosted on a static/edge platform (e.g. Vercel).
- **API + workers** hosted on a container platform that supports .NET (e.g. Railway).
- **PostgreSQL** as a managed database.
- Fittingly, DeployHub can eventually deploy itself.

## 9. Module layout (backend)

```
src/
├── DeployHub.Api/            # controllers, SignalR hub, startup, DI wiring
├── DeployHub.Core/           # interfaces, shared types, orchestrator, domain
├── DeployHub.Providers/      # IDeploymentProvider implementations
│   ├── Vercel/
│   ├── Railway/
│   └── Render/               # added later
├── DeployHub.Data/           # EF Core DbContext, entities, migrations
├── DeployHub.Infrastructure/ # encryption, email, GitHub client
└── DeployHub.Tests/          # unit + shared provider contract tests
```

The dependency rule: `Providers`, `Data`, and `Infrastructure` depend on `Core`. `Core` depends on nothing outward. This keeps the orchestrator pure and the providers swappable.

## 10. Module layout (frontend)

```
src/app/
├── auth/          # GitHub OAuth handling, guards, token storage
├── dashboard/     # project list
├── project/       # single project detail
├── deploy/        # deploy trigger + live log view
├── history/       # deployment history + log replay
├── settings/      # provider credentials, notifications
├── core/          # API client, SignalR service, models
└── shared/        # reusable UI components
```
