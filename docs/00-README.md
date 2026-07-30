# DeployHub / DeployAI — Project Documentation

> **Note:** The implementation uses the product name **DeployAI** (`DeployAI.*` projects, Angular app). These planning docs originally used the name DeployHub.

A unified deployment platform that lets non-technical users deploy their apps to multiple hosting providers (Vercel, Railway, Render, and more) from a single web interface.

## What this is

DeployHub is a web app where a user connects their GitHub account once, picks a repository, selects one or more hosting providers, and deploys with a single click — then watches live logs and reviews deployment history, all without touching a terminal.

The core design goal is **extensibility**: adding a new hosting provider should require writing one class and registering it, with no changes anywhere else in the system.

## Document index

| File | Contents |
|------|----------|
| `00-README.md` | This file — overview and index |
| `01-requirements.md` | Functional and non-functional requirements, user stories, scope |
| `02-architecture.md` | System architecture, provider plugin design, data flow |
| `03-data-model.md` | Database schema, entities, relationships, migrations |
| `04-api-spec.md` | REST/WebSocket API endpoints, request/response contracts |
| `05-implementation-plan.md` | Phased roadmap, timeline, milestones, testing strategy |
| `12-repository-scanning.md` | **Proposed.** One resolver for "where does this app live in the repo", replacing seven independent guesses |

## Tech stack at a glance

- **Frontend:** Angular 18 (standalone components, signals, RxJS)
- **Backend:** .NET 8 (ASP.NET Core Web API, minimal + controller hybrid)
- **Database:** PostgreSQL 16 (Entity Framework Core 8)
- **Background jobs:** Hangfire
- **Real-time:** SignalR (WebSocket log streaming)
- **Auth:** GitHub OAuth 2.0 + JWT session tokens

## Guiding principles

1. **Provider-agnostic core** — the deployment engine never contains provider-specific logic. All of that lives behind a single interface.
2. **Non-technical first** — every screen assumes the user does not know what a CLI, environment variable, or build command is unless they choose to look.
3. **Ship narrow, extend wide** — launch with two providers done well, then add the rest through the plugin system.
4. **Secure by default** — tokens encrypted at rest, scoped per user, never logged.

## Status

Planning phase. This documentation set defines the target before implementation begins.
