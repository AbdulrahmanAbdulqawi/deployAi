# DeployHub — API Specification

## 1. Conventions

- Base URL: `/api`
- All endpoints except the auth callback require a valid JWT in `Authorization: Bearer <token>`.
- Requests and responses are JSON unless noted.
- All timestamps are ISO 8601 UTC.
- Errors follow a consistent shape:

```json
{
  "error": {
    "code": "provider_token_invalid",
    "message": "Your Vercel token is no longer valid. Reconnect it in settings."
  }
}
```

Error messages are plain-language and safe to show to non-technical users. Raw exceptions are never returned.

## 2. Authentication

### GET /api/auth/github/login
Starts the GitHub OAuth flow. Redirects the browser to GitHub's authorization page with a signed `state` parameter.

### GET /api/auth/github/callback
GitHub redirects here with `code` and `state`.

- Validates `state`.
- Exchanges `code` for a GitHub access token.
- Creates or updates the user, stores the encrypted token.
- Issues a JWT access token + refresh token.
- Redirects to the SPA with the session established.

### POST /api/auth/refresh
Exchanges a refresh token for a new access token.

Request:
```json
{ "refreshToken": "..." }
```
Response:
```json
{ "accessToken": "...", "refreshToken": "...", "expiresIn": 900 }
```

### POST /api/auth/logout
Invalidates the refresh token.

## 3. Providers

### GET /api/providers
Returns the list of providers the system supports (driven by the provider registry, so it grows automatically).

Response:
```json
{
  "providers": [
    { "name": "vercel", "displayName": "Vercel", "apiStyle": "rest" },
    { "name": "railway", "displayName": "Railway", "apiStyle": "graphql" }
  ]
}
```

## 4. Provider credentials

### GET /api/credentials
Lists the current user's stored provider credentials (tokens never included).

Response:
```json
{
  "credentials": [
    {
      "id": "uuid",
      "providerName": "vercel",
      "label": "Personal",
      "isValid": true,
      "lastValidatedAt": "2026-01-10T12:00:00Z"
    }
  ]
}
```

### POST /api/credentials
Adds a provider token. The token is validated against the provider before saving.

Request:
```json
{ "providerName": "vercel", "label": "Personal", "token": "..." }
```
Response: `201 Created` with the credential summary (no token).

Errors: `provider_token_invalid` if validation fails.

### DELETE /api/credentials/{id}
Removes a stored credential. Fails if a deploy target still references it, with a clear message.

## 5. GitHub repositories

### GET /api/github/repos
Lists the authenticated user's GitHub repositories for project creation.

Query params: `?page=1&perPage=30&search=`

Response:
```json
{
  "repos": [
    { "fullName": "abdul/my-app", "defaultBranch": "main", "private": true }
  ],
  "page": 1,
  "hasMore": true
}
```

### GET /api/github/repos/{owner}/{repo}/branches
Lists branches for a repo so the user can pick one.

## 6. Projects

### GET /api/projects
Lists the user's projects with a summary of their latest deployment.

Response:
```json
{
  "projects": [
    {
      "id": "uuid",
      "name": "My App",
      "githubRepoFullName": "abdul/my-app",
      "defaultBranch": "main",
      "targets": [ { "providerName": "vercel" }, { "providerName": "railway" } ],
      "latestDeployment": {
        "id": "uuid",
        "status": "success",
        "completedAt": "2026-01-10T12:05:00Z"
      }
    }
  ]
}
```

### POST /api/projects
Creates a project.

Request:
```json
{
  "name": "My App",
  "githubRepoFullName": "abdul/my-app",
  "defaultBranch": "main",
  "targets": [
    {
      "providerName": "vercel",
      "credentialId": "uuid",
      "providerProjectId": "prj_abc",
      "config": {}
    }
  ]
}
```
Response: `201 Created` with the full project.

### GET /api/projects/{id}
Returns one project with its targets.

### PUT /api/projects/{id}
Updates name, branch, or targets.

### DELETE /api/projects/{id}
Deletes a project and its deployment history.

## 7. Deployments

### POST /api/projects/{id}/deployments
Triggers a deployment. Queues background jobs and returns immediately.

Request:
```json
{ "branch": "main" }
```
Response: `202 Accepted`
```json
{
  "deploymentId": "uuid",
  "status": "pending",
  "targets": [
    { "providerName": "vercel", "status": "pending" },
    { "providerName": "railway", "status": "pending" }
  ]
}
```

Meets NFR-2.1: responds in under 500ms because it only queues.

### GET /api/projects/{id}/deployments
Lists deployment history for a project, reverse chronological.

Query params: `?page=1&perPage=20`

Response:
```json
{
  "deployments": [
    {
      "id": "uuid",
      "branch": "main",
      "status": "success",
      "durationSeconds": 165,
      "startedAt": "2026-01-10T12:02:00Z",
      "completedAt": "2026-01-10T12:05:00Z",
      "targets": [
        { "providerName": "vercel", "status": "success", "deployUrl": "https://..." },
        { "providerName": "railway", "status": "success", "deployUrl": "https://..." }
      ]
    }
  ],
  "page": 1,
  "hasMore": false
}
```

### GET /api/deployments/{id}
Returns one deployment with all target statuses and URLs.

### GET /api/deployments/{id}/logs
Returns persisted logs for replay (used by history view).

Query params: `?target={deploymentTargetId}`

Response:
```json
{
  "logs": [
    { "providerName": "vercel", "sequence": 1, "line": "Building…", "loggedAt": "..." }
  ]
}
```

### POST /api/deployments/{id}/cancel
Requests cancellation of an in-progress deployment where the provider supports it.

## 8. Real-time logs (SignalR)

### Hub: /hubs/deployments

Client methods (server → client):
- `LogLine(deploymentId, providerName, sequence, line)` — a new log line.
- `StatusChanged(deploymentId, providerName, status)` — a status transition.
- `DeploymentCompleted(deploymentId, finalStatus)` — terminal event.

Server methods (client → server):
- `JoinDeployment(deploymentId)` — subscribe to a deployment's group.
- `LeaveDeployment(deploymentId)` — unsubscribe.

Transport falls back automatically from WebSocket to SSE to long polling (NFR reliability).

## 9. Notifications

### GET /api/notifications/preferences
Returns the user's preferences.

### PUT /api/notifications/preferences
Updates preferences.

Request:
```json
{ "emailOnSuccess": true, "emailOnFailure": true }
```

## 10. Webhooks (later phase)

### POST /api/webhooks/github
Receives GitHub push events. Verifies the HMAC-SHA256 signature, then triggers an auto-deploy for any project configured for that repo and branch. Unsigned or mismatched requests are rejected.

## 11. Rate limiting

- Deploy trigger: limited per user (e.g. 100/hour) — returns `429` with a clear message when exceeded.
- Provider calls in workers are throttled to respect each provider's documented limits, independent of the user-facing limit.
