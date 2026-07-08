# Split-origin deployment (Vercel + Railway)

## Vercel (frontend)
- `{{apiEnvKeysList}}` → Railway API URL (no trailing slash)
- `vercel.json` is SPA-only (no `/api` proxy rewrites)
- Build runs `scripts/write-api-env.mjs` before `ng build`
- `angular.json` production fileReplacements must swap in `environment.production.ts`
- `app.config.ts` must register `apiBaseInterceptor` so relative `/api/*` calls reach Railway

## Railway (API)
- `AllowedOrigins__0` → current production Vercel URL (update after domain changes)
- `AllowedOrigins__1` → additional preview origins as needed
- CORS must allow `*.vercel.app` preview deployments

## Auth
- Client auth path: `/api/v1/auth` (not `/api/Auth`)
- Refresh cookies: `SameSite=None; Secure` in Production
- Angular auth requests: `withCredentials: true`

## Why POST to `*.vercel.app/api/*` returns 405
- The Vercel host only serves the static SPA. API calls must be rewritten to Railway by `apiBaseInterceptor`.
