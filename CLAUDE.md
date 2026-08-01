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

Recorded so they get closed rather than re-done by hand. One line each — full narrative for
each lives in `docs/gaps/` (see `docs/gaps/README.md` for the index), following the same shape
as `docs/12-repository-scanning.md`. When you close or open a gap, update both: the one-liner
here, and the doc it links to.

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

### Repository scanning
- Ranking which sibling directory is the server (`ServerBuildProfileDiscovery`'s whole-repository case) is still a separate answer from the shared resolver — see `docs/12-repository-scanning.md`'s "What did not move" section directly rather than a copy here.
