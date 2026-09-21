# DeployAI MCP — Agent Instructions

DeployAI orchestrates **split-origin deployments**: Angular/React frontends on **Vercel** and .NET/Node backends on **Railway**, wired together via environment variables and proxy config.

## When to use DeployAI MCP vs provider MCP

| Use **DeployAI MCP** for | Use **Railway/Vercel MCP** for |
|--------------------------|--------------------------------|
| Project-scoped deploy workflows | Raw provider debugging |
| GitHub repo classification & readiness | Listing env vars / domains directly |
| Claude setup & fix agents | Low-level provider deploys |
| Post-deploy verification (CORS, split-origin) | Infrastructure inspection |

## Core concepts

- **Project** — links a GitHub repo to one or more **deploy targets** (Vercel website, Railway server).
- **Deployment** — a single deploy run across all targets for a branch/commit.
- **Deployment target** — per-provider run with its own status, logs, and optional `failureAnalysis`.
- **Verification** — HTTP probes after deploy (`website.reachable`, `server.health`, `connection.cors`, etc.).

## Typical workflows

### Deploy and monitor

1. `list_projects` or `get_project`
2. `trigger_deployment` → note `deploymentId`
3. Poll `get_deployment` until status is terminal (`succeeded`, `failed`, `partial`)
4. `get_deployment_logs` for build output

### Setup a new repo

1. `get_deployment_plan` — classify repo structure
2. `scan_deployment_readiness` — find missing files
3. `generate_deployment_setup` — Claude/template generates `vercel.json`, `railway.toml`, etc.
4. `merge_deployment_setup` — merge PR and sync env URLs

### Fix a failed build

1. `get_deployment` — check `targets[].failureAnalysis.canRequestClaudeFix`
2. If true: `generate_deployment_fix` with `deployment_id` and `target_id`
3. `merge_deployment_fix`

### Fix verification failure

1. `verify_deployment` with scope `both`
2. Find failed checks where `canRequestClaudeFix` is true
3. `generate_verification_fix` with `check_id`
4. `merge_deployment_fix`

## Authentication

Call `mcp_auth` if tools return `not_authenticated`. Options:

1. Pass `access_token` + `refresh_token` from DeployAI web app localStorage
2. Complete GitHub login in browser, then pass `callback_url` (full redirect URL with tokens)
3. Set `DEPLOYAI_ACCESS_TOKEN` and `DEPLOYAI_REFRESH_TOKEN` env vars

## Verification scopes

- `website` — frontend reachability, SPA shell, split-origin wiring
- `server` — API reachability, health endpoint
- `both` — all checks including CORS and API proxy

## Polling guidance

Deployments are async. After `trigger_deployment`, poll `get_deployment` every 10–30 seconds. Use `get_deployment_logs` for detailed build output.
