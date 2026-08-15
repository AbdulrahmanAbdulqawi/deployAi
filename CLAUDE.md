# DeployAI — working guidance

DeployAI is a non-technical deployment platform: connect GitHub once, link a provider, and
publish website + server in one flow. `docs/00-README.md` indexes the planning docs;
`docs/gaps/README.md` indexes what is known to be missing.

The rules in the second half of this file are not style preferences — they are the product
promise, and they decide what a change *should be*, not just how it should look. Read them
before proposing one.

## Where things are

| Path | What lives there |
|---|---|
| `src/DeployAI.Api` | Controllers, the bulk of the behaviour (`Services/`, ~18k lines), the SignalR hub, Hangfire jobs, and the deployment file templates |
| `src/DeployAI.Core` | Contracts and domain types only — provider interfaces, the deployment graph, plan and target config. No I/O, no provider SDKs |
| `src/DeployAI.Infrastructure` | GitHub reading and repository scanning, framework adapters (Dockerfile generation), OAuth, JWT, AES encryption, typed options |
| `src/DeployAI.Providers` | Provider implementations: Coolify, Railway, Vercel, and Hetzner object storage |
| `src/DeployAI.Providers.Railway.GraphQL` | Strawberry Shake typed Railway client, generated at build time from committed `Operations/**/*.graphql` + `schema.graphql` |
| `src/DeployAI.Data` | EF Core entities, `DeployAIDbContext`, migrations |
| `src/DeployAI.Tests` | xUnit — 103 test classes, just over 750 facts |
| `client/` | Angular 18 SPA |
| `docs/` | Planning docs (`00-README.md` is the index), `gaps/` narratives, the Coolify smoke test, the split-origin playbook |
| `.claude/skills/` | `diagnose-coolify-deploy`, `verify-deploy`, `curate-project-knowledge` — invoke these rather than re-deriving what they encode |
| `.cursor/rules/` | Four always-on conventions, summarised under "Code conventions" below |

Two structural details that surprise people:

- **`DeployAI.Providers.Railway.GraphQL` is not in `src/DeployAI.slnx`.** CI builds it as its own
  step, and that build *is* the check that every committed GraphQL operation still matches
  `schema.graphql`. Building or testing the solution will not catch a broken operation — see
  `docs/railway-graphql-schema.md`.
- **`src/Program.cs` and `src/Controllers/HealthController.cs` are orphans.** Namespace
  `DeployAI.Generated`, sitting under no project directory, compiled by nothing. They are
  template output that got committed; editing them has no effect on anything.

### Layering

`Api → Infrastructure + Providers → Core`, with `Data` referenced by `Api` and `Infrastructure`.
Core holds no I/O: a provider SDK, an `HttpClient`, or a `DbContext` appearing there is the
mistake to catch in review. Provider-specific behaviour lives behind an interface in
`Core/Providers` and is implemented in `Providers/` — per `docs/00-README.md`'s first guiding
principle, adding a provider should be one class plus a registration and nothing else.

### Vocabulary that keeps tripping people up

| Term | Meaning |
|---|---|
| `DeployTarget` | **Persistent.** One configured destination of a project (this app's server, on this Coolify instance). Carries `ConfigJson`. |
| `DeploymentTarget` | **Per-run.** One target's participation in one deployment. Carries status, logs, failure analysis. |
| `DeployTargetConfig` | The parsed `ConfigJson`: role, root/build/output directories, Dockerfile path, compose fields, and the `IsDatabaseTarget` / `IsStorageTarget` / `IsDeployableTarget` predicates. |
| Role | `website`, `server`, `database`, `storage` (`DeploymentPartRoles`). **Dispatch keys off role, never provider name** — two parts can share one Coolify instance and still be different things, and matching on provider collapses them into one. |
| Plan kind | `default`, `coolify-fullstack` (two apps, two domains, wired cross-origin), `coolify-compose` (one compose resource, single origin), `coolify-single` (`DeploymentPlanKind`). |
| Split vs single origin | Split origin needs cross-origin API URL injection and CORS wiring; `DeploymentPlanKindValues.IsSingleOrigin` is what says those checks must *not* run for a compose app. |

Storage is a role, not a deploy target: it is provisioned like a database and wired in as env
vars, so it must never appear in provider pickers or progress bars.

## How a deploy actually flows

1. **Scan** — `GET /api/github/repos/{owner}/{repo}/deployment-plan`. `RepositoryLayoutResolver`
   establishes where in the repo the app actually lives (see `docs/12-repository-scanning.md`);
   `FrontendBuildDetector`, `ServerBuildDetector`, `EnvVarDetector`, `DatabaseRequirementDetector`
   and `ObjectStorageNeedDetector` fill in what it needs. Every scan reports what it managed to
   read — `IsInconclusive` exists so "found nothing" and "could not look" stay distinguishable.
2. **Readiness and setup** — `DeploymentReadinessService` scores the repo against the chosen
   shape (`SplitOriginReadinessEvaluator` or `SingleOriginComposeReadinessEvaluator`).
   `DeploymentSetupService` generates the missing deployment files and opens a PR on the user's
   repo. `DeploymentFileGeneratorSelector` picks between `TemplateDeploymentFileGenerator`
   (files under `src/DeployAI.Api/DeploymentTemplates/`, catalogued in `catalog.json`) and
   `HybridDeploymentFileGenerator` (Claude, via `AnthropicMessageClient`), falling back to
   templates when no Anthropic key is configured rather than failing the flow.
3. **Project creation** — `POST /api/projects/from-plan` writes a `Project` and its
   `DeployTarget`s.
4. **Trigger** — `POST /api/projects/{id}/deployments` → `DeploymentOrchestrator.TriggerAsync`
   creates the `Deployment` plus one `DeploymentTarget` per deployable target, and enqueues one
   Hangfire job per target (`DeploymentJobRunner.RunAsync`).
5. **Pre-provider work, per target** — website targets get frontend env wiring and, where the
   framework inlines env at build time, a generated Dockerfile (Nixpacks builds see none of the
   app's environment). Server targets get their Dockerfile **regenerated on every deploy**, a
   required-configuration check, and object-storage auto-provisioning. All of this is
   *advisory*: each step is wrapped so a failure logs and continues rather than failing a deploy
   that would otherwise succeed — but every outcome that matters is written to the deploy log
   the user reads.
6. **Deploy** — `IProviderFactory.GetProvider(name)` → `TriggerDeploymentAsync`, then
   `GetStatusAsync` polling and `StreamLogsAsync`, each line persisted as a `DeploymentLog` and
   broadcast over SignalR (`/hubs/deployments`, JWT accepted via `access_token` query param).
7. **Finalize** — status aggregated across targets (`partial` when only some succeed),
   `RuntimeExceptionCheck` reads the running container for unhandled exceptions,
   `DeploymentVerificationService` exercises the deployed thing, and `DeploymentFailureAnalyzer`
   classifies any failure. A `code_build` classification is what makes the Claude fix flow
   available — `docs/deploy-failure-fix.md`.

Two recurring Hangfire jobs run alongside this, registered in `Program.cs`:
`EnvironmentDriftCheckJob` every six hours and `ProjectHealthMonitorJob` hourly.

## Development workflows

### Backend

```bash
dotnet build src/DeployAI.slnx
dotnet test  src/DeployAI.Tests/DeployAI.Tests.csproj
dotnet test  src/DeployAI.Tests/DeployAI.Tests.csproj --filter "FullyQualifiedName~RequiredConfigurationCheckTests"

# Separate step — validates every committed GraphQL operation against schema.graphql.
# Not part of the solution, so the commands above will not catch a broken operation.
dotnet build src/DeployAI.Providers.Railway.GraphQL/DeployAI.Providers.Railway.GraphQL.csproj
```

Run it locally:

```bash
docker compose up -d          # PostgreSQL 16 on :5432
cd src/DeployAI.Api && dotnet run   # :5000, Swagger in Development
```

The API creates the database if it is absent and applies EF migrations at startup — both are
skipped in the `Testing` environment, which also swaps Hangfire to memory storage. Add a
migration with:

```bash
dotnet ef migrations add <Name> --project src/DeployAI.Data --startup-project src/DeployAI.Api
```

Migrations are few and long-lived (9 as of writing). Before adding one, check the standing rule
on validating the chain — a migration that duplicates a table or lands out of order applies to
no database at all.

### Frontend

```bash
cd client
npm install
npm start                                                     # :4200, proxies /api + /hubs → :5000
npm test -- --browsers=ChromeHeadless --watch=false
npm test -- --include="**/your-file.spec.ts" --browsers=ChromeHeadless --watch=false
npm run build
npm run e2e                                                   # Playwright, client/e2e/
```

OAuth callbacks must hit the same origin as the SPA, so develop against `:4200` and let the
proxy forward — not against `:5000` directly.

In production the SPA and API are split-origin. `client/scripts/write-api-env.mjs` bakes
`NG_APP_API_URL` into `client/src/app/core/api-base.ts` at build time, and `apiBaseInterceptor`
prefixes `/api` and `/hubs` requests with it; `client/vercel.json` rewrites are the fallback.
`API_BASE_URL` is a **build-time** input — changing the variable requires a redeploy.

### CI

`.github/workflows/build.yml` runs on every push and PR to `main`/`master`: backend restore →
GraphQL schema validation → build → test, and frontend `npm ci` → unit tests → build. Both jobs
must be green. Per the standing rule below, a red build is fixed or deleted, never left running.

### Configuration and secrets

Settings come from `appsettings.json` with a gitignored `appsettings.Development.json` override,
or from environment variables using the `Section__Key` convention (`ConnectionStrings__Default`,
`GitHub__ClientSecret`, `Anthropic__ApiKey`). `README.md` lists the full set. Never ask a human
to paste a credential — that is a standing rule, not a preference.

## Code conventions

**Enums over strings** for any fixed value set — status, role, provider, category. Shared enums
live in `DeployAI.Core`; API-only sets can be `internal`. In TypeScript use string enums whose
values match the JSON, not union types. String *constants* alongside an enum
(`ProviderNameValues`, `DeploymentPlanKindValues`) are the established pattern for values that
are persisted or sent over the wire — extend those rather than introducing a bare literal.

**Tests ship with behaviour.** xUnit + Moq + `RichardSzalay.MockHttp`, mirroring the area under
test (`Services/`, `Providers/`, `GitHub/`, `Integration/`). Most names are underscore-separated
(`Apply_RefusesAmbiguousGraphs`, `StorageKeys_FollowTheConsumersConvention`); some newer ones
are full sentences describing the behaviour. Match the file you are in. Provider tests assert at
the boundary — request built, response parsed, against recorded or faked payloads — because CI
cannot call a real provider. Angular specs sit next to their source as `*.spec.ts`.

**Angular components are three files** — `.ts`, `.html`, `.scss` — never inline `template:` or
`styles:`. Components are standalone, routes are lazy-loaded, state lives in signal-based stores
under `core/stores/`. Use design tokens from `client/src/styles/_tokens.scss` rather than
hardcoded colours or `[data-theme='dark']` branches; no shadows or `backdrop-filter` on cards
and panels (overlays excepted).

**Providers are partial classes split by concern** — `CoolifyProvider.Management.cs`,
`.Database.cs`, `.ServiceOperations.cs`. A new capability is usually a new interface in
`Core/Providers` plus a new partial, not a wider `IDeploymentProvider`.

**Comments carry the incident, not the mechanics.** The distinctive habit in this codebase is
that a non-obvious guard explains which real failure produced it — see the Dockerfile
regeneration block in `DeploymentOrchestrator.RunAsync`, or the class summary on
`RequiredConfigurationCheckTests`. Public contracts in `Core` carry XML doc comments. When you
fix something subtle, write down what it cost.

**Files DeployAI writes into a user's repository are product surface.** Generated files must be
idempotent — regenerating with no change must produce no commit — and commit messages must name
what actually changed. `GeneratedDeploymentFileValidator` and `GeneratedDeploymentFilePathRules`
exist to enforce this; extend them rather than trusting a caller to be careful.

**Do not trust `README.md` on scope.** It describes only Vercel and Railway and never mentions
Coolify or Hetzner object storage, which are now central; `docs/00-README.md` still says
"Planning phase". Read the code, and treat the drift as recorded under Known gaps below.

## Core rule: if we do it by hand, DeployAI should do it

**Anything a human has to do in the Coolify UI, or over SSH on a Hetzner box, is a gap in
DeployAI — not a chore to repeat.** The product promise is that a non-technical user never
touches a terminal or a provider dashboard. Every manual step we perform ourselves is a step
that user would also have to perform, and cannot.

When a task requires going into Coolify or onto a Hetzner host, treat it as two pieces of work:

1. **Unblock** — do the manual thing if something is broken right now.
2. **Close the gap** — file or implement the DeployAI capability that would have made step 1
   unnecessary. Do not let step 1 happen twice without step 2.

If closing the gap is out of scope for the current change, say so explicitly and record it
rather than silently absorbing the manual step.

## Core rule, second half: if we fix it in one app, DeployAI should fix it for every app

The rule above is about steps taken in a provider's UI. This one is about edits made in a
deployed app's own repository, and it is the more expensive of the two, because the cost is
paid again per app rather than per incident.

**When a change is needed in an app DeployAI deploys, ask whether the next app would need the
same edit. If it would, the change belongs in DeployAI.** Fixing it in one repository and
moving on means the second app rediscovers the bug, the third rediscovers it again, and the
knowledge lives in whichever repository happened to hit it first — where no other app can
reach it.

Apply the same two steps: **unblock** the app that is broken now, then **close the gap** so no
app needs that edit again. What "close the gap" means depends on which of two kinds it is:

- **The same edit in every app → DeployAI writes it.** A Dockerfile, a storage adapter, an
  EF-migration step at container start, a health endpoint, the four Hetzner S3 quirks
  (`ForcePathStyle`, `WHEN_REQUIRED` checksums, SigV4, buffering non-seekable streams). None of
  this is any app's business logic; it is platform code that happens to live in the app's
  repository, and DeployAI already commits generated files, so it can own it.
- **The app's own logic → DeployAI detects the class.** It cannot write the fix and must not
  try. But a failure that reached production once will reach it again in another app, and
  DeployAI can nearly always catch the *shape*: an unhandled exception in the runtime log after
  a deploy, a configuration key the code declares and the target lacks, a bucket whose
  round-trip fails. Detection is the reflected change.

The test that keeps this honest: **could this fix have been written without knowing which app
it was for?** If yes, writing it in one app was the wrong place.

Worked example, both halves in one incident. Getting a single image upload working took four
fixes: SigV4 signing, unsigned-payload rejection, missing bucket CORS, and provisioning that
only ran at bucket creation. Three were platform code hand-written into one app's repository —
identical in every .NET app on Hetzner, and the reason the storage generator is a recorded gap
below. One, an EF query that could not translate, was that app's own logic — unfixable by
DeployAI, but it logged an unhandled exception on every request, which DeployAI can read.

## Rules are about DeployAI, never about a deployed app

Every rule here states what **DeployAI** does for **any** app it deploys. Rules are generic by
construction.

If a rule can only be written as "remember to do X in repo Y," it is not a rule — it is a
missing capability, and it belongs in the section below as a gap to close. A platform whose
correctness depends on the discipline of the person using it has not delivered its promise.
Our users are non-technical; they will not remember, and should not have to.

The same test applies to us. Guidance that reads *"be careful to…"* is a design smell. Ask what
DeployAI could detect, refuse, or repair automatically instead.

## Standing rules

**Detect divergence before deploying.** DeployAI knows the deployed ref and can read the
repository. When the branch being deployed has diverged from the user's other branches, say so
before deploying, in plain terms ("the branch you're deploying is 5 ahead and 22 behind your
local `main`"). Silent deploys of a stale ref let parallel lineages drift until the same feature
gets built twice — which is unmergeable rework, not a merge conflict.

**Validate the migration chain before deploying, not during.** Where an app uses migrations,
check that the chain actually applies: no two migrations creating the same table, no ordering
that puts a newer migration before an older one. A failed merge is recoverable; a migration
chain that applies to no database at all is a schema reconciliation project.

**Diagnose from the deployed artifact, never from the local repo.** Local code is not what is
running — usually not even the same commit. Establish, in order: which commit is deployed, what
routes it actually serves (an OpenAPI or route listing beats guessing), and what the container
logs say. Read source last, and only from the deployed ref. Probing invented paths produces
confident wrong answers. The `diagnose-coolify-deploy` skill encodes the order that works.

**Verification must exercise real usage.** A `/health` 200 proves a process is listening, not
that the app works. Treat a deployment as verified only when something a user would actually do
has been exercised. The `verify-deploy` skill is this rule as a checklist.

**New behaviour ships with tests — unit and integration.** Unit tests for logic that can be
exercised in isolation; integration tests for anything that crosses a boundary (HTTP endpoint,
database, provider API). Neither substitutes for the other: unit tests catch wrong logic,
integration tests catch wrong wiring, and most incidents here have been wiring.

For a bug fix, the test must be **shown to fail against the unfixed code** before the fix is
committed. Run it, watch it fail, then fix. A test written after the fix and never seen red
proves only that it agrees with the current implementation — it is decorative, and it will not
catch the regression it was written for.

Provider work cannot call real provider APIs in CI. Test at the boundary instead: assert the
request built and the response parsed, against recorded or faked payloads. "It can't be tested
because it talks to Coolify" means the seam is in the wrong place.

This rule is weaker than the others because it depends on discipline rather than enforcement —
by the standard above, that makes it a design smell. CI (`.github/workflows/build.yml`) runs the
suite on every PR to `main`, but nothing yet requires a change to arrive with tests. Closing
that is the real fix; until then, the rule is a promise we keep by hand.

**A red build that everyone ignores is worse than no build.** CI was failing on every run for
weeks — one stale Angular spec asserting a rendering the component had deliberately replaced —
so "the build is red" carried no information and nobody looked. Six merges went into an
already-failing pipeline before anyone checked. When CI breaks, fix it or delete the check;
leaving it red trains the team to stop reading the one signal that runs on every change.

**A fix has to reach the resources that already exist.** An operation that runs only when a
resource is created never runs again — so a fix lands on things made after it, and the person who
reported the bug still has it. Prefer re-running the operation on every deploy, idempotently, over
running it once at creation; "already set up" is not a reason to skip, it is the case that needs
checking. Observed five times in a single day: the server Dockerfile generated when an application
was created and never regenerated; duplicate env-var repair wired only to the database-linking
path; `ProvisionAsync` requiring a storage link nothing ever created; a bucket's CORS rule applied
only at bucket creation; and storage re-provisioning skipped whenever a link already existed. Two
of those were introduced and found the same day, which is the point — the shape is easy to write
and hard to see, so it wants a check that fails rather than a comment that informs.

**An absence must say which absence it is.** "Found nothing" and "could not look" are different
answers, and code that returns the same value for both turns a blind scan into a confident
negative. Every scan reports what it managed to read: `EnvScanResult.IsInconclusive`,
`RepositoryLayout.IsInconclusive`, and the storage verification's could-not-check line all exist
for this. A deploy may proceed on the first; it must never proceed silently on the second.

**Writes into a user's repository are a product surface.** Commits DeployAI authors live in
someone's history permanently. Messages must say specifically what changed, and generated files
must be idempotent — regenerating should produce no commit when nothing changed. Prefer opening
a PR over pushing directly to a default branch: a silent push to `main` is itself a cause of the
divergence described above.

**Never require a credential to be typed or pasted.** Every secret DeployAI needs should come
from a store it reads — an ignored local file, environment variables, or the provider's OAuth
flow. If a debugging session requires a human to paste a token, that is a gap. Prefer HTTPS
endpoints; treat any secret that has transited a chat or a plaintext channel as compromised and
rotate it.

## Known gaps

Recorded so they get closed rather than re-done by hand. One line each — full narrative for
each lives in `docs/gaps/` (see `docs/gaps/README.md` for the index), following the same shape
as `docs/12-repository-scanning.md`. When you close or open a gap, update both: the one-liner
here, and the doc it links to. The `curate-project-knowledge` skill is this loop as a checklist.

### Provisioning & environment variables — [docs/gaps/provisioning-and-env-vars.md](docs/gaps/provisioning-and-env-vars.md)
- Duplicate env-var repair only runs on the database-linking path, not every target.
- Env-var upserts still loop one key at a time in two callers instead of batching.
- ~~The managed environment store was project-wide~~ — closed; a secret on a website-only project still has nowhere else to go.
- CORS wiring is a guessed key-name list with nothing checking whether the guess was right.
- Secrets DeployAI generates exist only in DeployAI's own database, with no export path.

### Compose deployments — [docs/gaps/compose-deployments.md](docs/gaps/compose-deployments.md)
- Coolify's proxy labels freeze a stale port if ever custom-written; DeployAI must never write them.
- A compose deploy could silently collapse to just its frontend — closed for the silence, open for the manual "set up deployment files" step.
- Two unfinished implementations of compose generation exist side by side; neither is chosen.
- ~~Connection-string keys declared only in a compose environment block were invisible to detection~~ — closed (`e6e181e`).
- ~~A compose app never received a domain, so its Traefik route never existed~~ — closed (`5967a81`).

### Runtime diagnostics — [docs/gaps/runtime-diagnostics.md](docs/gaps/runtime-diagnostics.md)
- `runtime-logs` can't read a stopped container's crash — the exact case it exists for.
- Coolify's logs API only ever returns one container's output, with no way to pick another — a compose app's non-primary service is invisible to it.

### Verification & required config — [docs/gaps/verification-and-config-checks.md](docs/gaps/verification-and-config-checks.md)
- The wizard shows nothing different for an inconclusive env scan vs. a genuinely empty one.
- The required-configuration check warns but never blocks a deploy into a known crash-loop.
- Nothing flags a setting the app has that no code actually reads.
- Project status is never revalidated against the provider — a deleted app can still show healthy.
- No divergence warning, and no migration-chain validation, before a deploy.
- Verification is shallow for everything except object storage.
- Nothing requires a change to arrive with tests; CI runs the suite but doesn't gate on coverage.

### Database provisioning — [docs/gaps/database-provisioning.md](docs/gaps/database-provisioning.md)
- DeployAI can't create a Coolify project, only deploy into one that already exists.
- Only .NET apps get their schema/migrations applied automatically.
- A provisioned Coolify database is reachable from nothing outside its own container network.

### Object storage — [docs/gaps/object-storage.md](docs/gaps/object-storage.md)
- The file-storage layer is still hand-written per app, though it is platform code.
- An app is handed account-wide storage credentials far wider than it needs.

### Process — [docs/gaps/process.md](docs/gaps/process.md)
- ~~Generated commit messages were generic~~ — closed; the real cause was silently-failing no-op detection.
- `README.md` and `docs/00-README.md` describe a Vercel+Railway product still in "planning phase"; nothing keeps them honest as the code moves.

### Repository scanning
- Ranking which sibling directory is the server (`ServerBuildProfileDiscovery`'s whole-repository case) is still a separate answer from the shared resolver — see `docs/12-repository-scanning.md`'s "What did not move" section directly rather than a copy here.
