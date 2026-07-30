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
- **Only .NET apps get their schema created.** The generated .NET Dockerfile now bundles EF
  migrations and applies them before the app starts. Nothing equivalent exists for the other
  runtimes DeployAI deploys — a Node or Python service provisioned a database still meets an empty
  one, and the failure looks like a healthy app returning 500s.
- **Coolify's proxy labels are managed by Coolify, and DeployAI must not write them.** Writing
  `custom_labels` at all is what left two apps unroutable; the field is no longer sent. The problem
  that change was originally for is still open: Coolify caches the Traefik labels it generates at
  first deploy, so a later build-pack or port change does not reach the live proxy until someone
  presses "Reset Labels to Defaults" and redeploys.
- **The wizard still shows nothing for an inconclusive env scan.** `env-schema` now finds config
  wherever the app actually lives, flags `inconclusive` for both "read no sources" and "could not
  list the repository", and returns `projectDirectory` / `searchedIn` so a wrong answer is
  recognisable. The wizard ignores all of it: an empty result and an unreadable repository still
  render the same — no environment step — so a user can still deploy with nothing set and no
  warning. This is the last piece of the `Jwt configuration missing` crash-loop still open, and it
  is now a UI change rather than a detection problem. The screen has also **not been exercised**
  against the new input: nested repos will start producing variables where they produced none.
- **Ranking which sibling directory is the server is still a separate answer.** Every scanner now
  reads through `RepositoryLayoutResolver` (`docs/12-repository-scanning.md`), which closes the
  root-only class of silent failure — but one caller could not move wholesale.
  `ServerBuildProfileDiscovery` asks a question the resolver does not answer: given a whole
  repository, *which* of several sibling directories is the server. It answers by scoring names and
  excluding known frontend directories, because the resolver takes the first directory holding any
  application manifest and would nominate `client/package.json`. That ranking cannot move into the
  resolver — the storage and configuration scans must read the frontend, and skipping it there is
  the same bug pointed the other way. So two answers to "where is the app" still exist, and the
  discovery half is exercised only when a repository is first classified. A test asserts the
  divergence so it stays deliberate.
- **The required-configuration check warns; it does not stop a deploy.** `RequiredConfigurationCheck`
  now compares what the deployed ref declares it needs against what the target actually has, and
  names the difference in the deploy log before the app starts — the three incidents it was built
  from (`Jwt`, `Storage`, `Tickets`) would each have been named rather than discovered by a
  crash-loop. It is deliberately advisory: "required" means a key the app declares with no value of
  its own, which is a strong signal and not proof, since a value can arrive from somewhere DeployAI
  cannot see. Two things remain — it reads only the server target (a website with required config is
  unchecked), and nobody has to act on the warning, so a deploy can still proceed into a known
  crash-loop.
- **Runtime logs are unavailable exactly when they are needed.** `runtime-logs` returns
  "Application is not running" for a stopped container, so the capability added for "an app that
  builds fine but crash-loops" cannot read the crash. It took a `lifecycle/start` first, and a
  container that has hit Coolify's restart limit stays stopped until something starts it.
- **Project status is never revalidated against the provider.** A project whose Coolify
  applications have been deleted still shows as deployed and healthy, with links to domains that
  return 404. The status and URLs come from the last deployment record and are never rechecked, so
  the dashboard can advertise a dead app indefinitely.
- **No divergence warning.** DeployAI deploys whatever ref it is pointed at without reporting
  how that ref relates to the user's other branches.
- **No migration-chain validation.** Nothing checks for colliding or misordered migrations
  before a deploy.
- **Verification is shallow for everything except storage.** A deployment probing `/health`
  successfully can still have a fully broken API surface. See `DeploymentVerificationService.cs` /
  `DeploymentEndpointProbes.cs`. Object storage is now the exception and the template: it does a
  signed write-read-delete and a real browser preflight on every deploy, and reports whether it
  passed, failed, or could not run. Databases, the API's routes and CORS deserve the same treatment.
- **DeployAI's managed environment store is project-wide, but the apps it writes to are not.**
  `Project.EnvironmentVariablesEncrypted` is one blob per project, so a listed variable carries no
  record of which container it was pushed to. Adding one now asks (the add row has a target picker
  and defaults to the server), but editing and deleting still fall through to the API's default,
  which is the *website* — so removing a server-side variable can delete DeployAI's record of it
  while leaving the value live on the server, and saving a new value can write it to the frontend.
  The fix is to store the target alongside each variable and show it in the list; until then the
  list is a set of names whose location is unknowable from the UI.
- **CORS wiring is a guess, and nothing checks whether the guess was right.**
  `ResolveServerCorsEnvKeys` writes a fixed list of key names per framework. It now includes ASP.NET's
  own `Cors__Origins__0` / `Cors__AllowedOrigins__0` alongside DeployAI's `App__*` convention, but it
  is still a list of names hoped to match. An app reading any other key gets its origins written
  nowhere it looks, keeps whatever hardcoded fallback its source has, and nothing reports it: the API
  logs nothing (the browser never sends the request), both deploy targets go green, `/health` passes,
  and the only symptom is the frontend's own "cannot reach the server" message. One `OPTIONS` from the
  website origin to the API, checking for `Access-Control-Allow-Origin`, would settle it in a single
  request at the end of a deploy — that is the fix; the key list is a stopgap. Better still: read the
  key out of the repository (`GetSection("...")` in `Program.cs`) rather than guessing names at all,
  which the resolver now makes possible. **The pattern to copy already exists**: `ObjectStorageVerifier`
  sends exactly that preflight against the bucket and reports the result on every deploy. Doing the
  same from the website origin to the API is the same shape of work.
- **An app is handed credentials far wider than it needs, and DeployAI cannot narrow them.**
  `ObjectStorageEnvironmentWiring` writes the storage connection's own access key and secret onto the
  app, so a container that needs one bucket can list, create and delete every bucket in the project.
  They are written as secrets, so Coolify will not read them back, but an app compromise reaches the
  whole storage account rather than its own data.
  **Researched, so it does not need investigating again:** Hetzner issues S3 credentials per project
  and [each key pair is valid for every bucket in it](https://docs.hetzner.com/storage/object-storage/faq/s3-credentials/);
  scoping requires a second key pair plus a bucket policy allowlisting it, and key pairs
  [can only be generated in Hetzner's console](https://docs.hetzner.com/storage/object-storage/getting-started/generating-s3-keys/) —
  there is no API, so DeployAI cannot mint one. The mitigation is therefore user-side: a key pair (or
  project) per app, then a bucket policy. DeployAI already supports it — several `ObjectStorage`
  connections can exist and each storage target records which one it belongs to — but nothing guides
  a user there. What it does now is refuse to hide the exposure: provisioning reports how many
  buckets the credentials it just wired can reach. Applying the bucket policy automatically once a
  second key exists is the remaining work.
- **Secrets DeployAI generates exist only in DeployAI.** `Jwt__SigningKey`, `Tickets__SigningKey` and
  the storage keys are written to the provider as secrets, which Coolify will not return, and stored
  encrypted in DeployAI's own database. If that database is lost or reset — which happened once
  already this week — the values are unrecoverable, every issued token and ticket signature breaks,
  and there is no export path. Generating secrets on a user's behalf implies keeping them
  recoverable.
- **Generated commit messages are generic.** Dockerfile generation has produced several commits
  sharing one message, obscuring what each changed.
- **Nothing requires a change to arrive with tests.** CI runs the suite but does not fail a PR
  that adds behaviour without covering it, so the testing rule above rests on discipline alone.
