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
- **The file-storage layer is still hand-written per app, and it is platform code.** The clearest
  outstanding case of the second core rule. DeployAI provisions the bucket, wires the five keys,
  and verifies the round trip — then every app writes its own client against them, and every app
  rediscovers the same four Hetzner quirks: `ForcePathStyle`, `RequestChecksumCalculation` /
  `ResponseChecksumValidation` set to `WHEN_REQUIRED`, SigV4 (Ceph rejects unsigned payloads), and
  buffering a non-seekable upload stream before signing. All four failed silently in one app in one
  session. Two apps in this account now have two different implementations, one of them weaker.
  DeployAI already commits generated files into a repository, so it can generate this the way it
  generates Dockerfiles: a storage service, an image pipeline, and a proxy endpoint so bytes go
  through the API and the bucket stays private. Two rules constrain it — `UseS3` must require *all*
  the settings so a blank value falls back to local disk rather than half-configured S3, and the
  composition-root patch must refuse rather than guess when its anchor is missing. Rewriting an
  app's own upload call sites is out of scope and must be stated in the PR, not assumed done.
- **Coolify's proxy labels are managed by Coolify, and DeployAI must not write them.** Writing
  `custom_labels` at all is what left two apps unroutable; the field is no longer sent. The stale-label
  problem is still open and now has a worked example. Mirqab's app carried a *custom* label set
  pinned to `loadbalancer.server.port=80` from the day it was created. Every later correction —
  build pack, Node version, port — reached Coolify and none reached the proxy, so the container ran,
  the deploy reported success and every request got 502 from Traefik while nginx logged no requests
  at all. **An empty Labels box is the healthy state**: Coolify regenerates labels from
  `ports_exposes` on each deploy, and the app that works has nothing in that box. A custom set is
  what freezes the port. Recovery took Coolify's own "Reset Labels to Defaults" *and* a redeploy —
  one deploy after the reset was not enough, and the labels stayed empty until the deploy after that.
  What DeployAI could do without writing labels itself: notice that the port it just set differs from
  the one the proxy is using and say so, rather than reporting a green deploy for an unreachable site.
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
  compares what the deployed ref declares it needs against what the target actually has, and names
  the difference in the deploy log before the app starts. It also names whole sections that only
  `appsettings.Development.json` declares and the app has nothing from — added after finding that on
  yemenConnect, `Jwt`, `Tickets` and `Bootstrap` appear *only* in that file, so the check built for
  the `Jwt configuration missing` crash-loop was blind to `Jwt` on the very repository that
  crash-looped. It is deliberately advisory: "required" means a key the app declares with no value of
  its own, which is a strong signal and not proof, since a value can arrive from somewhere DeployAI
  cannot see. Two things remain — it reads only the server target (a website with required config is
  unchecked), and nobody has to act on the warning, so a deploy can still proceed into a known
  crash-loop.

- **Nothing reports a setting the app has that no code reads.** The mirror of the check above.
  yemenConnect carries `Jwt__Key` and `Jwt__Secret` on its API; `JwtOptions` binds only `Issuer`,
  `Audience`, `SigningKey` and `AccessTokenMinutes`, so both are dead weight that reads as
  configured. The obvious rule — "flag any key the repository never declares" — was tried and
  discarded: on this app it produces zero true positives and flags DeployAI's own
  `ConnectionStrings__Default` conventions, and noise is what teaches people to skip the line that
  matters. A sound version needs to read the options classes, not the settings files.
- **Runtime logs are unavailable exactly when they are needed.** `runtime-logs` returns
  "Application is not running" for a stopped container, so the capability added for "an app that
  builds fine but crash-loops" cannot read the crash. It took a `lifecycle/start` first, and a
  container that has hit Coolify's restart limit stays stopped until something starts it. For a
  *running* container it works well — it is what diagnosed the `/public/stats` 500 above, returning
  the full EF translation error and the SQL around it in one call.
- **DeployAI provisions a Coolify database it then cannot reach.** `IProviderDataServiceInspection`
  is implemented by `RailwayProvider` only, so `data-info` answers `unsupported_provider` for every
  Coolify database — which is the default. The connection string DeployAI writes onto the app uses
  Coolify's internal Docker hostname, reachable only from inside that network, so nothing outside the
  container can look at the data: not the tables panel, not a migration check, not the user. Found
  while trying to remove three throwaway accounts a test had created; the only routes to them are SSH
  onto the Hetzner host or an endpoint in the app itself, and the first is precisely what the core
  rule says must not become routine. A provisioned database nobody can inspect is also why "did the
  migrations apply" still has no answer for the provider DeployAI defaults to.
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
  **The app's own output is now read on every deploy** (`RuntimeExceptionCheck`), which closes the
  general case rather than one route: an application that is failing says so, in its own words,
  whatever language it is written in. It found the second confirmed instance — yemenConnect's
  `/public/stats`, the endpoint its landing page calls on every visit, 500ing on every request for
  the life of the deployment while `/health` returned 200 and both targets went green. Route probing
  could not have: the app's OpenAPI document sits behind the same fallback auth policy as everything
  else (`401`), so there is nothing to enumerate from outside.
  **It is scanned before the build as well as after, and the "before" is the half that matters most:**
  the outgoing container has served real traffic, so its log holds the failures only real usage
  produces. A route that 500s for every visitor logs nothing until a visitor arrives, so an
  after-only check catches crashes at startup and misses everything request-shaped.
  What remains: nothing acts on the finding — a deploy proceeds, and an app that logged a thousand
  errors an hour deploys as quietly as one that logged none.
- **~~DeployAI's managed environment store is project-wide.~~ Closed.** `ProjectEnvironmentStore`
  keys by target as well as name, so a website and a server that both carry `API_URL` no longer share
  one record — saving on one used to overwrite the other's value and deleting from one erased both.
  The old flat blob still reads: those entries land in a bucket belonging to no app, are still
  exported, and move to a real target the first time one is saved or deleted there, so an existing
  project converges without a migration that would have to guess. What remains is upstream of the
  store: a project whose only deployable target is a website has nowhere else for a server secret to
  go, which is how four `MIRQAB_*` secrets ended up on a frontend and were then baked into its image
  as build args by Coolify's Nixpacks builder. The store no longer confuses them; nothing yet warns
  that a secret is on the wrong kind of app.
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
- **~~Generated commit messages are generic.~~ Closed, and the cause was worse than the symptom.**
  Dockerfile generation produced several commits sharing one message because it produced several
  commits *that changed nothing*. Both paths meant to skip an unchanged file; neither did.
  `ContentMatches` base64-decoded its argument, but `GetFileMetadataAsync` already decodes — so it
  decoded plain text, threw `FormatException`, swallowed it, and answered "different" every single
  time. The server path did not call it at all. Four deploys of one app in one afternoon appended
  four empty commits to a user's own history. Messages now distinguish Add from Update and name the
  website build separately, but the real fix is that an unchanged file produces no commit.
  **The shape to watch for:** a `catch` whose fallback is the unsafe answer is invisible. It cannot
  fail loudly, it cannot be seen in a log, and the only evidence is junk arriving somewhere nobody
  is looking — here, someone else's git history.
- **Nothing requires a change to arrive with tests.** CI runs the suite but does not fail a PR
  that adds behaviour without covering it, so the testing rule above rests on discipline alone.
