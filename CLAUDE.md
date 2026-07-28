# DeployAI — working guidance

DeployAI is a non-technical deployment platform: connect GitHub once, link a provider, and
publish website + server in one flow. See `README.md` for the stack and `docs/00-README.md`
for the document index.

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
confident wrong answers.

**Verification must exercise real usage.** A `/health` 200 proves a process is listening, not
that the app works. Treat a deployment as verified only when something a user would actually do
has been exercised.

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

Recorded so they get closed rather than re-done by hand.

- **Duplicate repair only runs on the database-linking path.** The write race is fixed --
  `CoolifyProvider.UpsertEnvVarAsync` now goes through Coolify's `PATCH /envs/bulk`, which
  resolves by key server-side, instead of the old non-atomic list-then-create. Repair now exists
  too: `ReconcileDuplicateEnvVarsAsync` deletes every record after the first for a key, which is
  the only safe rule because Coolify's bulk handler resolves with `->where('key', $key)->first()`
  and so writes to the first record and leaves later ones stale. But it is only wired into
  `LinkDatabaseVariablesAsync`. An application that carries duplicates and never gets a database
  link is still never repaired, and there is no way to ask for a repair without deploying. One app
  was observed with 32 records for 16 keys, including two `DATABASE_URL`s pointing at *different*
  Postgres instances -- and the stale copies pointed at a database that no longer existed at all.
- **Callers still upsert one key at a time.** `UpsertEnvVarsAsync` can apply a whole set in a
  single request, but `FrontendEnvironmentWiringService` and `CoolifyProvider.Database` still
  loop key by key. Batching would cut N round trips to one and leave no window for a concurrent
  sync to interleave mid-set.
- **DeployAI cannot create a Coolify project, only deploy into an existing one.** A missing
  environment is now created rather than dead-ending, but the project picker still only offers
  projects that already exist. Coolify exposes `POST /projects`, so nothing prevents closing this
  — until then, the first deploy into a new Coolify instance is a manual step.
- **Project status is never revalidated against the provider.** A project whose Coolify
  applications have been deleted still shows as deployed and healthy, with links to domains that
  return 404. The status and URLs come from the last deployment record and are never rechecked, so
  the dashboard can advertise a dead app indefinitely.
- **No divergence warning.** DeployAI deploys whatever ref it is pointed at without reporting
  how that ref relates to the user's other branches.
- **No migration-chain validation.** Nothing checks for colliding or misordered migrations
  before a deploy.
- **Verification is shallow.** A deployment probing `/health` successfully can still have a
  fully broken API surface. See `DeploymentVerificationService.cs` / `DeploymentEndpointProbes.cs`.
- **Generated commit messages are generic.** Dockerfile generation has produced several commits
  sharing one message, obscuring what each changed.
- **Nothing requires a change to arrive with tests.** CI runs the suite but does not fail a PR
  that adds behaviour without covering it, so the testing rule above rests on discipline alone.
