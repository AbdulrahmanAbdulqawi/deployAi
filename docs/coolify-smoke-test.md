# Coolify full-stack manual smoke test

Use this checklist when validating DeployAI against a real Coolify instance. No live Coolify credentials are required in CI; run these steps manually after connecting your instance in Settings → Connections.

## Prerequisites

- A Coolify instance reachable from DeployAI (HTTPS, valid API token)
- A GitHub repository with Angular frontend (`client/`) and .NET API (`src/Api/` or similar)
- DeployAI connected to GitHub and Coolify

## 1. Connection and infrastructure

1. Add a Coolify credential in DeployAI (Settings → Connections).
2. Confirm provider health shows Coolify as **self-hosted**.
3. In the project wizard, choose **Coolify full-stack** deployment mode.
4. Select or create a Coolify project, server, environment, and GitHub app.
5. Pick or create separate website and API applications on Coolify.

**Pass:** Both apps appear in the wizard and can be selected without errors.

## 2. Readiness scorecard

1. Open the wizard deploy plan step for an unprepared Angular + .NET repo.
2. Confirm the scorecard describes **Coolify full-stack setup** (not Vercel + Railway).
3. Confirm blocking gaps include `write-api-env.mjs`, `api-base.interceptor.ts`, and `HealthController.cs`.
4. Confirm gaps do **not** require `vercel.json` or `railway.toml`.

**Pass:** Scorecard reflects Coolify providers and omits Vercel/Railway-only files.

## 3. Deployment setup scaffold

1. With missing readiness files, confirm the **Generate setup** panel is visible.
2. Run setup (template or AI mode).
3. Open the generated PR and verify it includes:
   - `client/scripts/write-api-env.mjs` (fails build when API URL env is missing)
   - `client/src/app/core/interceptors/api-base.interceptor.ts`
   - `docs/DEPLOYMENT.md` with Coolify env var names
   - CORS guidance for `AllowedOrigins__0` / `App__FrontendUrl`
4. Confirm the PR title/body mention **Coolify full-stack**, not Vercel + Railway.

**Pass:** PR contains Coolify-specific scaffold files and messaging.

## 4. Deploy and environment wiring

1. Merge the setup PR (or fix gaps manually).
2. Create the DeployAI project and trigger the first deploy.
3. After both apps deploy, run **Sync environment** (or wait for post-deploy sync).
4. On the Coolify **website** app, verify `DEPLOYAI_API_URL` or `API_BASE_URL` points at the live API URL.
5. On the Coolify **API** app, verify:
   - `AllowedOrigins__0` = website URL
   - `App__FrontendUrl` = website URL

**Pass:** Cross-provider env vars match live URLs without trailing-slash mismatches.

## 5. Runtime verification

1. Open the website URL in a browser.
2. Perform a login or any API call that uses relative `/api/*` paths.
3. Confirm requests reach the API host (network tab shows API domain, not website domain).
4. Hit `GET {api-url}/api/v1/health` — expect HTTP 200.
5. If using auth cookies, confirm login works cross-origin (`withCredentials`, `SameSite=None`).

**Pass:** No 405 responses from the website host; health check succeeds; auth works if applicable.

## 6. Regression checks

- Re-run readiness scan on the prepared repo → **ready** with no blocking gaps.
- Re-deploy without changing env vars → no drift warnings.
- Change website domain in Coolify → re-sync env → CORS still allows the new origin.

## Automated coverage (no live Coolify)

These areas are covered by unit tests instead of live smoke:

- `DeploymentTemplateResolverTests` — Coolify scenario resolution
- `DeploymentFileScaffolderIntegrationTests` — Coolify scaffold file generation
- `SplitOriginReadinessEvaluatorTests` — Coolify CORS and file path expectations
- `FrontendEnvironmentWiringServiceTests` — Coolify env application
- `readiness-scorecard.spec.ts` — Coolify scorecard and setup panel visibility

Run locally:

```bash
dotnet test src/DeployAI.Tests/DeployAI.Tests.csproj --filter "FullyQualifiedName~DeploymentTemplate|FullyQualifiedName~SplitOrigin|FullyQualifiedName~DeploymentSetup|FullyQualifiedName~ClaudeDeployment"
cd client && npm test -- --include="**/readiness-scorecard.spec.ts" --browsers=ChromeHeadless --watch=false
```
