# DeployHub — Requirements

## 1. Vision

Give non-technical users (indie founders, freelancers, small teams, creators) a single place to deploy their applications to whichever hosting providers they use, without learning each provider's dashboard, CLI, or token system.

The value proposition: **connect once, deploy everywhere.**

## 2. Target users

| Persona | Description | Primary need |
|---------|-------------|--------------|
| Non-technical founder | Has a GitHub repo built by a contractor or AI tools; needs it live | One-click deploy, clear status |
| Freelancer | Manages several client projects across providers | Central dashboard, per-project separation |
| Small team | 2–5 people shipping a product | Shared visibility, notifications, history |
| Indie creator | Ships side projects frequently | Speed, low friction, multi-provider |

## 3. Scope

### In scope (MVP)

- GitHub OAuth login and repository listing
- Project creation (link a repo + branch to one or more providers)
- One-click deploy to Vercel and Railway
- Live deployment logs (real-time)
- Deployment status tracking (pending, in progress, success, failed)
- Deployment history per project
- Email notification on deploy completion
- Encrypted storage of provider credentials

### In scope (later phases)

- Additional providers (Render, Netlify, Heroku, Fly.io)
- Simultaneous multi-provider deploys from one click
- Environment variable management UI
- Rollback to a previous deployment
- Auto-deploy on GitHub push (webhook toggle)
- Slack notifications
- Team management and roles
- Deployment analytics

### Out of scope

- Building or hosting infrastructure ourselves (we orchestrate third-party providers)
- Source code editing or IDE features
- Domain registration (we may surface provider domain settings later, not sell domains)
- Billing/payment processing for the user's own apps

## 4. Functional requirements

### 4.1 Authentication

- **FR-1.1** Users authenticate via GitHub OAuth 2.0.
- **FR-1.2** The system stores an encrypted GitHub access token per user.
- **FR-1.3** Sessions are maintained via JWT (short-lived access token + refresh token).
- **FR-1.4** Users can disconnect GitHub and revoke stored tokens from settings.

### 4.2 Provider credentials

- **FR-2.1** Users add provider API tokens (Vercel, Railway, etc.) through a settings screen.
- **FR-2.2** Tokens are validated against the provider before saving.
- **FR-2.3** Tokens are encrypted at rest (AES-256) and never returned to the client in plaintext.
- **FR-2.4** Users can update or delete provider tokens at any time.

### 4.3 Projects

- **FR-3.1** Users create a project by selecting a GitHub repository and a branch.
- **FR-3.2** A project can target one or more providers.
- **FR-3.3** Each provider target stores provider-specific configuration (project id, build settings) as flexible JSON.
- **FR-3.4** Users can edit or delete projects.
- **FR-3.5** Users only see and manage projects they own.

### 4.4 Deployments

- **FR-4.1** Users trigger a deployment for a project with one action.
- **FR-4.2** The system queues the deployment and returns immediately with a deployment id.
- **FR-4.3** Deployments run as background jobs; the UI is never blocked.
- **FR-4.4** Each deployment records status transitions and timestamps.
- **FR-4.5** When a project targets multiple providers, each provider deploy is tracked independently but grouped under one deployment record.

### 4.5 Live logs

- **FR-5.1** Users see real-time logs while a deployment runs.
- **FR-5.2** Logs stream over WebSocket (SignalR) with automatic fallback to polling.
- **FR-5.3** Logs are persisted so they can be reviewed after completion.

### 4.6 History

- **FR-6.1** Each project shows a reverse-chronological list of past deployments.
- **FR-6.2** History entries show branch, status, duration, timestamp, and which provider(s).
- **FR-6.3** Users can open any past deployment to view its stored logs.

### 4.7 Notifications

- **FR-7.1** Users receive an email when a deployment completes (success or failure).
- **FR-7.2** Notification preferences are configurable per user.

### 4.8 Provider extensibility

- **FR-8.1** New providers are added by implementing a single interface and registering the implementation.
- **FR-8.2** Adding a provider requires no changes to the orchestrator, API layer, or database schema.
- **FR-8.3** The frontend discovers available providers dynamically from the backend.

## 5. Non-functional requirements

### 5.1 Security

- **NFR-1.1** All credentials encrypted at rest with AES-256.
- **NFR-1.2** All traffic over HTTPS/TLS in production.
- **NFR-1.3** Tokens never written to logs or error messages.
- **NFR-1.4** GitHub webhook signatures verified with HMAC-SHA256.
- **NFR-1.5** Every API endpoint enforces authentication and per-user authorization.

### 5.2 Performance

- **NFR-2.1** Deploy trigger endpoint responds in under 500ms (queues, does not wait for build).
- **NFR-2.2** Log latency from provider to user under 2 seconds.
- **NFR-2.3** Dashboard loads project list in under 1 second for up to 100 projects.

### 5.3 Reliability

- **NFR-3.1** Failed provider API calls retry with exponential backoff.
- **NFR-3.2** Provider rate limits are respected via queue throttling.
- **NFR-3.3** A provider outage affects only that provider, not the whole deploy.

### 5.4 Scalability

- **NFR-4.1** Background job system supports concurrent deployments across users.
- **NFR-4.2** Architecture supports horizontal scaling of the API and worker tiers.

### 5.5 Usability

- **NFR-5.1** A non-technical user can go from login to first successful deploy without documentation.
- **NFR-5.2** Error messages are plain-language and actionable, never raw stack traces.
- **NFR-5.3** The interface works on modern desktop and mobile browsers.

### 5.6 Maintainability

- **NFR-6.1** Provider logic is isolated; each provider is independently testable.
- **NFR-6.2** All providers are exercised by the same shared contract test suite.

## 6. User stories

- As a founder, I want to log in with GitHub so I don't have to create another password.
- As a founder, I want to pick my repo from a list so I don't have to type URLs.
- As a freelancer, I want each client project separated so I don't mix deployments.
- As a user, I want to click deploy and watch it happen so I know it's working.
- As a user, I want an email when it's done so I don't have to keep watching.
- As a team member, I want to see past deployments so I can tell what changed and when.
- As a power user, I want to add a new provider myself so I'm not locked into two.

## 7. Assumptions and constraints

- Users already have accounts with the hosting providers they want to use.
- Users can obtain a provider API token (we document how for each provider).
- Provider public APIs remain available and reasonably stable.
- MVP targets Vercel (REST) and Railway (GraphQL) to prove the abstraction across two very different API styles.

## 8. Success criteria

- A non-technical user completes a first deploy to both providers in under 10 minutes.
- Adding a third provider (Render) takes under one day of development.
- Zero credential leaks in logs or client responses.
- 95% of deployments report accurate final status.
