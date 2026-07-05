# DeployHub — Implementation Plan

## 1. Strategy

Build the provider abstraction first and prove it against two deliberately different APIs (Vercel REST, Railway GraphQL). Once the abstraction holds across both, every later provider is a small, low-risk addition. Ship a narrow but complete MVP, then extend.

## 2. Phases

### Phase 0 — Foundations (Week 1)

Goal: skeletons that run and talk to each other.

- Backend solution scaffold with the module layout (`Api`, `Core`, `Providers`, `Data`, `Infrastructure`, `Tests`).
- Angular 18 app scaffold with routing and the module layout.
- PostgreSQL provisioned; EF Core `DbContext` and initial migration for all MVP tables.
- Health-check endpoint and a "hello" call from SPA to API to confirm the pipe.

Deliverable: empty but wired full stack.

### Phase 1 — Authentication (Week 1–2)

Goal: a user can log in with GitHub.

- GitHub OAuth login + callback endpoints.
- Encrypted GitHub token storage (value converter + key from secret manager).
- JWT issuance and refresh.
- Angular auth flow, route guards, token storage, and a minimal logged-in shell.

Deliverable: log in, see an empty dashboard, log out.

### Phase 2 — Provider abstraction + Vercel (Week 3–4)

Goal: prove the plugin design with the first real provider.

- Define `IDeploymentProvider` and all shared types in `Core`.
- Implement `ProviderFactory` and registration.
- Implement `VercelProvider` (REST): validate, list projects, trigger, status, logs.
- Credential vault endpoints; add and validate a Vercel token.
- `GET /api/providers` driven by the registry.
- Shared provider contract test suite (runs against any provider).

Deliverable: add a Vercel token, list Vercel projects.

### Phase 3 — Projects + deploy + live logs (Week 4–5)

Goal: an end-to-end deploy to Vercel with live logs.

- Project CRUD (repo + branch + target).
- GitHub repo/branch listing endpoints and pickers in the UI.
- Deployment orchestrator + Hangfire jobs.
- SignalR hub; workers push logs; SPA shows the live log view.
- Persist logs and final status; deployment history list + replay.

Deliverable: click deploy, watch it run on Vercel, see it in history.

### Phase 4 — Railway (Week 5–6)

Goal: prove the abstraction across a different API style.

- Implement `RailwayProvider` (GraphQL): validate, list, trigger, status, logs.
- Register it; confirm it appears in `GET /api/providers` and the UI with no orchestrator changes.
- Run the shared contract tests against Railway.
- Support a project targeting both providers; group results under one deployment.

Deliverable: deploy the same project to Vercel and Railway together.

### Phase 5 — Notifications + polish + hardening (Week 7)

Goal: production-ready MVP.

- Email notifications on completion + preferences UI.
- Plain-language error handling end to end.
- Rate limiting and provider throttling.
- Retry with exponential backoff for provider calls.
- UI refinement, empty states, loading states, mobile pass.
- Unit + integration test coverage; security review of token handling.

Deliverable: MVP ready to launch.

### Phase 6 — Launch (Week 8)

- Deploy frontend to an edge/static host; API + workers to a .NET-capable host; managed PostgreSQL.
- Monitoring, logging, error tracking.
- Onboarding docs for obtaining provider tokens.
- Collect first-user feedback.

Deliverable: live MVP with Vercel + Railway.

## 3. Post-MVP roadmap

| When | Item | Effort |
|------|------|--------|
| Month 2 | Render provider (REST) | ~1 day (new provider file + registration) |
| Month 2 | Netlify provider | ~1–2 days |
| Month 3 | Environment variable management UI | Medium |
| Month 3 | Rollback to previous deployment | Medium |
| Month 3 | Auto-deploy on push (GitHub webhooks) | Medium |
| Month 4 | Slack notifications | Small |
| Month 4 | Team management and roles | Large |
| Month 5+ | Analytics, more providers (Fly.io, Heroku) | Ongoing |

Each new provider is intentionally the same small unit of work, which is the payoff of the Phase 2 abstraction.

## 4. Testing strategy

### Unit tests
- Each provider tested in isolation with a mocked HTTP/GraphQL client.
- Orchestrator tested against a fake `IDeploymentProvider`.

### Shared contract tests
- One suite defines the behavior every provider must satisfy (trigger returns an id, status maps correctly, invalid credentials throw the right error).
- Run the suite against every registered provider so new providers must conform before they ship.

### Integration tests
- Auth flow, project CRUD, deploy trigger → job → status update, against a test database.

### End-to-end (pre-launch)
- Full path: login → add token → create project → deploy → live logs → history, in a staging environment against provider sandboxes where available.

## 5. Risks and mitigations

| Risk | Mitigation |
|------|------------|
| Railway GraphQL differs enough to strain the abstraction | It's the point of Phase 4 — done early to surface abstraction gaps while there's time to adjust the contract |
| Provider API changes | Isolated per provider; a break affects one file, and contract tests catch regressions |
| Provider rate limits | Worker-side throttling + exponential backoff |
| Token leakage | Encryption at rest, never logged, never returned; security review in Phase 5 |
| Log streaming differences (push vs poll) | The `StreamLogsAsync` async-stream contract hides whether a provider pushes or is polled |
| Scope creep into 5 providers at launch | Hard rule: MVP is exactly two providers; others go through the plugin post-launch |

## 6. Definition of done (MVP)

- A non-technical user can log in, connect Vercel and Railway, create a project, deploy to both, watch live logs, and see history — without documentation.
- Adding Render is demonstrably a single-file change plus registration.
- No credentials appear in logs or client responses.
- Core paths covered by automated tests, including the shared provider contract suite.

## 7. Team and stack fit

The stack (Angular 18 + .NET 8 + PostgreSQL) matches existing strengths, so effort concentrates on the interesting parts — the provider abstraction, real-time logs, and the non-technical UX — rather than on learning tools. The result also stands as a strong portfolio piece: multi-provider integration, background processing, real-time streaming, and a clean extensibility model.
