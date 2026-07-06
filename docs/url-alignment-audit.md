# DeployAI URL Alignment Audit Report

**Date:** 2026-07-06  
**Scope:** DeployAI self-hosted production (Vercel client + Railway API)

## Live endpoints discovered

| Role | Provider | URL |
|------|----------|-----|
| Client (Angular) | Vercel | `https://deployai-mu.vercel.app` |
| Client aliases | Vercel | `https://deployai-abdulrahmanabdulqawi76-7865s-projects.vercel.app` |
| API (.NET) | Railway | `https://deployai-api-production.up.railway.app` |

**Vercel project:** `deployai` (`prj_MbWxX7qIgcpXCpTCLxPo9Uaqwx2B`), root directory `client`, Angular preset, output `dist/client/browser`.

**Railway project:** `deployai-api` (`40e207ef-2cd0-4ebf-b7a4-d640c6f88679`), service `deployai-api` (`0f05def7-6338-4b14-a333-f70bab7b618f`), port 8080, latest deploy **SUCCESS** (2026-07-06 08:26 UTC).

---

## Alignment matrix

| Check | Expected (recommended same-origin pattern) | Railway / API | Vercel / client | Pass? |
|-------|------------------------------------------|---------------|-----------------|-------|
| API public URL | Reachable JSON health | `https://deployai-api-production.up.railway.app` | — | **Yes** |
| Client public URL | SPA loads | — | `https://deployai-mu.vercel.app` | **Yes** |
| `App__FrontendUrl` | `https://deployai-mu.vercel.app` | **Not set** (defaults to `http://localhost:4200`) | — | **No** |
| `App__ApiUrl` | `https://deployai-mu.vercel.app` (with Vercel rewrites) | **Not set** (defaults to `http://localhost:5000`) | — | **No** |
| `GitHub__ClientId` / `GitHub__ClientSecret` | Real GitHub OAuth app values | **Not set** — live redirect uses placeholder `your-github-client-id` | — | **No** |
| `Jwt__Secret` / `Encryption__Key` | Strong production secrets | **Not visible** in service variables | — | **Unknown / likely No** |
| Vercel `/api` rewrite | Proxy to Railway API | — | **Missing** — [`client/vercel.json`](../client/vercel.json) only has SPA fallback | **No** |
| Vercel `/hubs` rewrite | Proxy to Railway SignalR | — | **Missing** | **No** |
| Client `GET /api/health` | JSON `{"status":"ok"}` | — | Returns **HTML** (index.html) | **No** |
| CORS (`Access-Control-Allow-Origin`) | `https://deployai-mu.vercel.app` when cross-origin | Only `http://localhost:4200` allowed | — | **No** |
| GitHub OAuth `redirect_uri` | Production callback URL | Live value: `http://localhost:5000/api/auth/github/callback` | Vercel `/api/auth/*` serves SPA (no API) | **No** |
| Vercel Integration redirect | `App__ApiUrl` + `/api/auth/vercel/callback` | Not configured | README still references stale `api-production-65ec` URL | **No** |
| Railway OAuth redirect | `App__ApiUrl` + `/api/auth/railway/callback` | Not configured | — | **No** |
| Vercel env vars | Optional for Angular (uses relative `/api`) | — | **None** configured | N/A |
| DB connection | Postgres on Railway | `ConnectionStrings__Default` → `deployai` DB | — | **Yes** |

---

## Live validation results

### 1. Health check

| URL | Status | Content-Type | Body |
|-----|--------|--------------|------|
| `https://deployai-mu.vercel.app/api/health` | 200 | `text/html` | Angular `index.html` |
| `https://deployai-api-production.up.railway.app/api/health` | 200 | `application/json` | `{"status":"ok","service":"DeployAI"}` |

### 2. CORS

```
GET /api/health
Origin: https://deployai-mu.vercel.app
→ 200 JSON, no Access-Control-Allow-Origin header

GET /api/health
Origin: http://localhost:4200
→ 200 JSON, Access-Control-Allow-Origin: http://localhost:4200
```

Cross-origin browser calls from the Vercel client to the Railway API **will fail** unless Vercel rewrites make `/api` same-origin.

### 3. OAuth (GitHub login entrypoint)

| URL | Result |
|-----|--------|
| `https://deployai-mu.vercel.app/api/auth/github/login` | 200 HTML (SPA) — **not routed to API** |
| `https://deployai-api-production.up.railway.app/api/auth/github/login` | 302 → GitHub with `client_id=your-github-client-id` and `redirect_uri=http://localhost:5000/api/auth/github/callback` |

Production OAuth is **not wired** for either the recommended same-origin or cross-origin pattern.

### 4. SignalR

`POST /hubs/deployments/negotiate` on Railway returns **401** without JWT (expected). From the Vercel origin, `/hubs/*` would hit the SPA fallback today — WebSocket would not reach the API.

### 5. Railway service health

| Service | Status | Latest deploy |
|---------|--------|---------------|
| deployai-api | SUCCESS | 2026-07-06 08:26 UTC |
| Postgres | SUCCESS | 2026-07-06 08:13 UTC |

Deploy logs show migrations applied and app started; no `App__` configuration in startup output.

---

## Root cause summary

DeployAI production is running as **two isolated deployments** without URL wiring:

1. **Vercel client** uses relative `/api` and `/hubs` paths but has **no rewrites** to Railway — all non-file routes fall through to `index.html`.
2. **Railway API** still uses **development defaults** from [`appsettings.json`](../src/DeployAI.Api/appsettings.json) for `App:FrontendUrl`, `App:ApiUrl`, and GitHub OAuth — because `App__*`, `GitHub__*`, `Jwt__*`, and `Encryption__*` are not set in Railway variables.

The product already solves a similar problem for **user apps** via [`FrontendEnvironmentWiringService`](../src/DeployAI.Api/Services/FrontendEnvironmentWiringService.cs) (Railway deploy URL → Vercel env vars), but **DeployAI itself** has no equivalent self-host wiring.

---

## Recommended fix (same-origin pattern — matches local dev)

This pattern requires **no Angular code changes** (client keeps relative `/api` and `/hubs`).

### Vercel (`client/vercel.json`)

Add rewrites **before** the SPA fallback:

```json
{
  "rewrites": [
    { "source": "/api/:path*", "destination": "https://deployai-api-production.up.railway.app/api/:path*" },
    { "source": "/hubs/:path*", "destination": "https://deployai-api-production.up.railway.app/hubs/:path*" },
    { "source": "/(.*)", "destination": "/index.html" }
  ]
}
```

Redeploy the Vercel project after committing.

### Railway (deployai-api service variables)

| Variable | Value |
|----------|-------|
| `App__FrontendUrl` | `https://deployai-mu.vercel.app` |
| `App__ApiUrl` | `https://deployai-mu.vercel.app` |
| `GitHub__ClientId` | _(from GitHub OAuth app)_ |
| `GitHub__ClientSecret` | _(from GitHub OAuth app)_ |
| `Jwt__Secret` | _(long random string)_ |
| `Encryption__Key` | _(long random string)_ |
| `Vercel__ClientId` | _(if using Vercel Integration OAuth)_ |
| `Vercel__ClientSecret` | _(if using Vercel Integration OAuth)_ |
| `Vercel__IntegrationSlug` | _(if using Vercel Integration OAuth)_ |
| `Railway__ClientId` | _(if using Railway OAuth)_ |
| `Railway__ClientSecret` | _(if using Railway OAuth)_ |

### External OAuth provider settings

Register these redirect URIs (all on the **Vercel** origin with same-origin rewrites):

| Provider | Redirect URI |
|----------|--------------|
| GitHub OAuth app | `https://deployai-mu.vercel.app/api/auth/github/callback` |
| Vercel Integration | `https://deployai-mu.vercel.app/api/auth/vercel/callback` |
| Railway OAuth app | `https://deployai-mu.vercel.app/api/auth/railway/callback` |

Update stale README production URL (`api-production-65ec`) to match live endpoints.

---

## Alternative pattern (cross-origin — not recommended without code changes)

- Set `App__ApiUrl` = Railway URL, `App__FrontendUrl` = Vercel URL
- Configure CORS on Railway for the Vercel origin
- Add absolute API base URL support in Angular (not implemented today)
- Register OAuth callbacks on the Railway URL

---

## Product automation backlog (for user apps + self-host)

Prioritized by impact observed in this audit:

| Priority | Gap | Proposed DeployAI feature |
|----------|-----|---------------------------|
| P0 | Vercel SPAs with relative `/api` get HTML instead of API | When website target uses relative API paths, generate or patch `vercel.json` rewrites from Railway `DeployUrl` at project creation / first deploy |
| P0 | Railway API doesn't know frontend URL for CORS / OAuth redirects | After Vercel website deploy succeeds, upsert Railway env vars (`App__FrontendUrl` or framework-specific CORS list) from Vercel production URL |
| P1 | No post-deploy URL verification | Add orchestrator step: `GET {client}/api/health` or ping Railway health after wiring; surface mismatch in deployment UI |
| P1 | Framework env key coverage | Extend `ResolveApiEnvKeys` as new stacks are added; document Angular same-origin rewrite pattern |
| P2 | Self-host template | Ship committed `vercel.json` rewrites + Railway env var checklist for DeployAI operators |
| P2 | Startup config guard | API logs warning when `App__FrontendUrl` or `App__ApiUrl` still point at localhost in production |
| P3 | README / docs drift | Auto-generate or CI-check production URL docs against linked provider projects |

---

## MCP / tooling notes

- **Railway MCP** (`user-railway`): authenticated; use `environment_id` `602caf88-891a-48d4-8044-120e57e0bf02` for `list_variables` / `list_domains` (linked env id can differ from tool output).
- **Vercel MCP**: added in [`.cursor/mcp.json`](../.cursor/mcp.json) (`https://mcp.vercel.com`). Restart Cursor and complete OAuth login to use MCP tools. This audit used **Vercel CLI** as fallback (`vercel inspect`, `vercel project inspect`, `vercel env ls`).
- **Vercel project linked** locally: `client/.vercel/project.json` (gitignored).

---

## Next steps (operator checklist)

1. Set Railway variables listed above (secrets via Railway dashboard, not git).
2. Add Vercel rewrites and redeploy client.
3. Update GitHub / Vercel Integration / Railway OAuth redirect URIs to the Vercel origin.
4. Re-run validation:
   - `https://deployai-mu.vercel.app/api/health` → JSON
   - GitHub login from Vercel client → redirects to GitHub with correct `redirect_uri`
   - Browser DevTools: API calls and SignalR connect without CORS errors
5. Decide which P0 product automation items to implement in DeployAI for user deployments.
