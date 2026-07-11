# Coolify full-stack deployment (Angular + .NET)

## Website app (Angular on Coolify)
- `{{apiEnvKeysList}}` → API URL (no trailing slash)
- Build runs `scripts/write-api-env.mjs` before `ng build`
- `angular.json` production fileReplacements must swap in `environment.production.ts`
- `app.config.ts` must register `apiBaseInterceptor` so relative `/api/*` calls reach the API app

## API app (.NET on Coolify)
- `AllowedOrigins__0` → current production website URL (update after domain changes)
- `App__FrontendUrl` → website URL for CORS and redirects
- `App__BaseUrl` → website URL

## Auth
- Client auth path: `/api/v1/auth` (not `/api/Auth`)
- Refresh cookies: `SameSite=None; Secure` in Production
- Angular auth requests: `withCredentials: true`

## Why POST to the website host `/api/*` fails
- The website host only serves the static SPA. API calls must be prefixed by `apiBaseInterceptor` to reach the API app.
