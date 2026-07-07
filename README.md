# DeployAI

DeployAI is a non-technical deployment platform. Connect GitHub once, link Vercel and Railway, pick folders in a monorepo, and publish website + server in one flow with live activity and history.

## Stack

- **Frontend:** Angular 18 (`client/`)
- **Backend:** .NET 8 Web API (`src/DeployAI.Api`)
- **Claude fix builds:** Production API image (`src/Dockerfile`) includes .NET SDK 8 and Node.js 20 so fix generation can run `dotnet build` / `npm run build` locally before opening a PR
- **Database:** PostgreSQL 16
- **Jobs:** Hangfire
- **Real-time:** SignalR

## Prerequisites

- .NET 8 SDK
- Node.js 20+
- Docker Desktop (for PostgreSQL) or a local PostgreSQL instance
- GitHub OAuth app
- Vercel Integration (OAuth) — see below
- Railway OAuth App — see below

## Quick start

### 1. Start PostgreSQL

```bash
docker compose up -d
```

### 2. Configure the API

Edit `src/DeployAI.Api/appsettings.json` (or use environment variables):

| Setting | Description |
|---------|-------------|
| `ConnectionStrings:Default` | PostgreSQL connection string |
| `GitHub:ClientId` | GitHub OAuth app client ID |
| `GitHub:ClientSecret` | GitHub OAuth app client secret |
| `Jwt:Secret` | Long random string for JWT signing |
| `Encryption:Key` | Long random string for AES-256 token encryption |
| `App:FrontendUrl` | Angular dev URL (`http://localhost:4200`) |
| `App:ApiUrl` | Public API URL used in OAuth callbacks (dev: `http://localhost:4200` via proxy) |
| `Vercel:ClientId` | Vercel Integration OAuth client ID |
| `Vercel:ClientSecret` | Vercel Integration OAuth client secret |
| `Vercel:IntegrationSlug` | Slug from your Vercel Integration URL |
| `Vercel:CallbackPath` | `/api/auth/vercel/callback` |
| `Railway:ClientId` | Railway OAuth App client ID |
| `Railway:ClientSecret` | Railway OAuth App client secret |
| `Railway:CallbackPath` | `/api/auth/railway/callback` |
| `Railway:Scopes` | `openid email profile offline_access workspace:admin` |

Create a GitHub OAuth app with callback URL:

```text
http://localhost:4200/api/auth/github/callback
```

For local development, run the Angular app on port 4200 (it proxies `/api` to the .NET API). OAuth callbacks must hit the same origin as the SPA.

### Vercel Integration (one-time setup)

Create a [Vercel Integration](https://vercel.com/docs/integrations/create-integration/vercel-api-integrations) in the Vercel dashboard:

| Setting | Local dev | Production |
|---------|-----------|------------|
| Redirect URL | `http://localhost:4200/api/auth/vercel/callback` | `https://api-production-65ec.up.railway.app/api/auth/vercel/callback` |
| Scopes | `user`, `project`, `project-env-vars`, `deployment` | same |

Copy the Integration **Client ID**, **Client Secret**, and **slug** (from the integration install URL) into config:

```json
"Vercel": {
  "ClientId": "your-vercel-integration-client-id",
  "ClientSecret": "your-vercel-integration-client-secret",
  "IntegrationSlug": "your-vercel-integration-slug",
  "CallbackPath": "/api/auth/vercel/callback"
}
```

Users connect via **Connect with Vercel** in Connections (OAuth). Manual token paste remains available under Advanced.

### Railway OAuth App (one-time setup)

In your Railway workspace, open **Developer** → **New OAuth App** and register:

- **Redirect URI:** `http://localhost:4200/api/auth/railway/callback` (same origin as the Angular dev server; production uses your public `App:ApiUrl` + callback path)

Copy the client ID and secret into `appsettings.Development.json`:

```json
"Railway": {
  "ClientId": "your-railway-oauth-client-id",
  "ClientSecret": "your-railway-oauth-client-secret",
  "CallbackPath": "/api/auth/railway/callback",
  "Scopes": "openid email profile offline_access workspace:admin"
}
```

Users connect via **Connect with Railway** in Connections (OAuth). Manual token paste remains available under Advanced.

Ensure your Railway account has the GitHub repo connected when creating services from the wizard.

### 3. Run the API

```bash
cd src/DeployAI.Api
dotnet run
```

The API applies EF Core migrations on startup and listens on `http://localhost:5000`.

### 4. Run the Angular app

```bash
cd client
npm install
npm start
```

Open `http://localhost:4200`.

## User flow (v1)

1. **Continue with GitHub** on the welcome screen
2. **Connections** → **Connect with Vercel** and/or **Connect Railway**
3. **Add your first app** → pick a GitHub app and version
4. **Which parts?** → choose Website (Vercel), Server (Railway), or both; browse folders in your repo
5. **Where should it live?** → pick or create destinations on each provider
6. **Publish** → one click deploys all selected parts; watch live activity per provider
7. **App settings** on the project page → manage Vercel env vars (when website is included)
8. **Past updates** → review history and replay activity

## Project layout

```text
src/
  DeployAI.Api/            REST + SignalR + Hangfire
  DeployAI.Core/           Provider contracts, domain types
  DeployAI.Providers/      Vercel + Railway providers
  DeployAI.Data/           EF Core entities + migrations
  DeployAI.Infrastructure/ GitHub OAuth, JWT, encryption
  DeployAI.Tests/          Contract + integration tests
client/                    Angular SPA
docs/                      Planning documents
```

## Tests

```bash
cd src
dotnet test DeployAI.slnx
```

## Environment variables

You can override settings with environment variables using the standard ASP.NET Core convention, for example:

- `ConnectionStrings__Default`
- `GitHub__ClientId`
- `GitHub__ClientSecret`
- `Jwt__Secret`
- `Encryption__Key`
- `App__FrontendUrl`
- `App__ApiUrl`
- `Vercel__ClientId`
- `Vercel__ClientSecret`
- `Vercel__IntegrationSlug`
- `Railway__ClientId`
- `Railway__ClientSecret`

## v1 scope

- GitHub login with encrypted token storage
- Vercel OAuth connect + encrypted credentials (manual token fallback)
- Railway OAuth connect + encrypted credentials (manual token fallback)
- Monorepo wizard: GitHub folder picker for website/server parts
- Vercel project creation (with root folder) and env var management via REST API
- Railway service creation and deploy via GraphQL API
- Dual-target projects: publish website + server in one action
- Background publish pipeline with Hangfire
- Live activity via SignalR + persisted logs
- History and replay

Email notifications and production hardening are planned for v1.1.
