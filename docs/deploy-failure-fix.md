# Deploy failure fix with Claude

When a publish fails because of a **code or build error** (TypeScript, Angular, .NET compile, npm build), DeployAI analyzes the deployment logs automatically and can suggest a Claude-generated fix.

## When the fix panel appears

After a failed or partial deployment, DeployAI classifies each failed target's logs:

| Category | Claude fix offered? |
|----------|---------------------|
| `code_build` | Yes |
| `infrastructure` | No (GitHub link, credentials, provider limits) |
| `unknown` | No |

The publish view shows **Generate fix with Claude** only when `canRequestClaudeFix` is true.

## Workflow

1. Publish fails with a build error in Vercel or Railway logs.
2. DeployAI saves a failure analysis on the deployment target.
3. On the publish screen, review the summary and log excerpt.
4. Click **Generate fix with Claude** (requires `Anthropic:ApiKey` in API config).
5. DeployAI opens a `deployai/fix-*` branch and pull request against the deployment branch.
6. Review the PR, merge it, then publish again.

## Configuration

```json
"Anthropic": {
  "ApiKey": "your-key",
  "Model": "claude-sonnet-4-20250514"
}
```

Without an API key, setup file generation falls back to templates, but **deploy fixes require Claude** and return `claude_not_configured`.

## API

- `POST /api/deployments/{id}/targets/{targetId}/fix` — generate fix PR
- `POST /api/github/repos/{owner}/{repo}/deployment-fix/merge` — merge fix PR

SignalR event `FailureAnalysisReady` updates the publish view when analysis completes.
