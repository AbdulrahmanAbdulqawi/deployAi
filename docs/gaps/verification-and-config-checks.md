# Verification and configuration checks

**Status:** the required-configuration check and `RuntimeExceptionCheck` are real, working
capabilities — but every one of them is advisory. Nothing here yet stops a deploy from
proceeding into a known-bad state; each only makes the bad state visible in the log.

## The wizard still shows nothing for an inconclusive env scan

`env-schema` now finds config wherever the app actually lives, flags `inconclusive` for both
"read no sources" and "could not list the repository", and returns `projectDirectory` /
`searchedIn` so a wrong answer is recognisable. The wizard ignores all of it: an empty result
and an unreadable repository still render the same — no environment step — so a user can still
deploy with nothing set and no warning. This is the last piece of the `Jwt configuration
missing` crash-loop still open, and it is now a UI change rather than a detection problem. The
screen has also **not been exercised** against the new input: nested repos will start producing
variables where they produced none.

## The required-configuration check warns; it does not stop a deploy

`RequiredConfigurationCheck` compares what the deployed ref declares it needs against what the
target actually has, and names the difference in the deploy log before the app starts. It also
names whole sections that only `appsettings.Development.json` declares and the app has nothing
from — added after finding that on one app, `Jwt`, `Tickets` and `Bootstrap` appear *only* in
that file, so the check built for a `Jwt configuration missing` crash-loop was blind to `Jwt` on
the very repository that crash-looped.

It is deliberately advisory: "required" means a key the app declares with no value of its own,
which is a strong signal and not proof, since a value can arrive from somewhere DeployAI cannot
see. Two things remain — it reads only the server target (a website with required config is
unchecked), and nobody has to act on the warning, so a deploy can still proceed into a known
crash-loop.

## Nothing reports a setting the app has that no code reads

The mirror of the check above. One app carried `Jwt__Key` and `Jwt__Secret` on its API;
`JwtOptions` binds only `Issuer`, `Audience`, `SigningKey` and `AccessTokenMinutes`, so both are
dead weight that reads as configured. The obvious rule — "flag any key the repository never
declares" — was tried and discarded: on this app it produces zero true positives and flags
DeployAI's own `ConnectionStrings__Default` conventions, and noise is what teaches people to skip
the line that matters. A sound version needs to read the options classes, not the settings files.

## Project status is never revalidated against the provider

A project whose Coolify applications have been deleted still shows as deployed and healthy, with
links to domains that return 404. The status and URLs come from the last deployment record and
are never rechecked, so the dashboard can advertise a dead app indefinitely.

## No divergence warning

DeployAI deploys whatever ref it is pointed at without reporting how that ref relates to the
user's other branches. See the standing rule "Detect divergence before deploying" in `CLAUDE.md`
— this is that rule, unimplemented.

## No migration-chain validation

Nothing checks for colliding or misordered migrations before a deploy. See the standing rule
"Validate the migration chain before deploying, not during" in `CLAUDE.md` — same relationship.

## Verification is shallow for everything except storage

A deployment probing `/health` successfully can still have a fully broken API surface. See
`DeploymentVerificationService.cs` / `DeploymentEndpointProbes.cs`. Object storage is the
exception and the template: it does a signed write-read-delete and a real browser preflight on
every deploy, and reports whether it passed, failed, or could not run. Databases, the API's
routes and CORS deserve the same treatment.

**The app's own output is now read on every deploy** (`RuntimeExceptionCheck`), which closes the
general case rather than one route: an application that is failing says so, in its own words,
whatever language it is written in. It found a confirmed instance on a second app — the endpoint
its landing page calls on every visit, 500ing on every request for the life of the deployment
while `/health` returned 200 and both targets went green. Route probing could not have: the
app's OpenAPI document sits behind the same fallback auth policy as everything else (`401`), so
there is nothing to enumerate from outside.

**It is scanned before the build as well as after, and the "before" is the half that matters
most:** the outgoing container has served real traffic, so its log holds the failures only real
usage produces. A route that 500s for every visitor logs nothing until a visitor arrives, so an
after-only check catches crashes at startup and misses everything request-shaped. See
`docs/gaps/runtime-diagnostics.md` for the limits of this same check against a compose app with
more than one container.

**What remains**: nothing acts on the finding — a deploy proceeds, and an app that logged a
thousand errors an hour deploys as quietly as one that logged none.

## Nothing requires a change to arrive with tests

CI runs the suite but does not fail a PR that adds behaviour without covering it, so the testing
rule in `CLAUDE.md`'s Standing Rules rests on discipline alone.

## Closed: the fleet sweep (2026-08-17)

Verification used to be computed and thrown away. The hourly `ProjectHealthMonitorJob` ran the eight
URL checks, reduced them to a pass count on `Project.HealthJson`, and discarded the detail — so the
only question anyone actually asks, *"was this working yesterday?"*, had no answer anywhere.

Three tables now hold it: `project_verification_runs` (one per project per sweep),
`project_verification_check_results` (the history, indexed by project + check + time), and
`project_check_states` (the current picture and the notification ledger). `Project.HealthJson`
survives as a derived cache so the project list and health banner still render without a join.

**Two bugs were found in the old sweep and were watched failing before being fixed.** It looped with
no try/catch, so one unreachable provider abandoned every project ordered after it; and it returned
silently for any project without a `success` deployment, so the projects most likely to be broken
were the ones it never looked at. Neither recurring job had a single test, which is why both
survived. `EnvironmentDriftCheckJob` had the identical shape and moved onto the same isolated,
scope-per-project runner — fixing one and leaving the other is the mistake this repository's own
rules warn about.

**What the sweep asks now**, beyond the URL probes: whether the provider still has the application
(`provider.application_exists`), whether the connection still works (`provider.connection`), what the
app itself is logging (`runtime.exceptions`, promoted from a deploy-log line into a verdict), whether
the settings the deployed code declares are still set (`config.required`) and still hold the values
the last deploy saw (`config.drift`), and whether every `Active` domain still resolves and serves a
valid certificate (`domain.certificate`, `domain.dns`).

The configuration checks cost no GitHub calls. `RequiredConfigurationCheck` already worked out which
settings the code declares with no value of its own, and now records that in
`target_config_manifests` — including, deliberately, when the scan was inconclusive, so a blind read
cannot harden into a confident "nothing is required". The sweep compares the manifest against one
provider listing. Values are stored only as per-target HMAC fingerprints: drift needs "changed", not
"changed to what", and a monitoring table is no place for anyone's secrets.

**A fifth status carries the whole thing.** `VerificationCheckStatus.Inconclusive` is the absence rule
made into a type, and `CheckLedgerTransitions` is what keeps it honest: an inconclusive observation
never moves `LastConclusiveStatus`, never moves `StatusChangedAt`, and never touches the notification
ledger. Without that, a network blip mid-outage sends "recovered" when nothing recovered, and
announces the same outage twice when it clears.

### What the first live run found

Run against the real fleet on 2026-08-17, three projects, no seeded data:

- **`yemeni-breeze` — Failed: "The application this app deploys to no longer exists on Coolify."**
  This is the gap itself, caught on the first sweep by an application that had genuinely been deleted
  while the dashboard went on reporting the project as deployed. No deliberate breakage was needed.
- **`Mirqab` — Failed: "exists on coolify but is not running (exited:unhealthy)."** A container Coolify
  had given up restarting. Present and dead is a different answer from absent, and both are failures.
- **Both then reported `runtime.exceptions` as inconclusive**, naming *"Application not found"* and
  *"Application is not running"* — not the clean-log pass that an empty finding list would otherwise
  have produced. That distinction is the entire point of the type.
- **`yemenConnect` rolled up Healthy with an inconclusive named in its summary** — *"5 of 6 checks
  passed; 1 could not be checked"* — rather than the inconclusive silently counting as a pass.
- **The ledger held**: `provider.application_exists` notified once and stayed silent across the next
  two sweeps while still failing; every inconclusive row kept an empty `LastConclusiveStatus` and an
  empty `LastNotifiedStatus`; and checks blind for three consecutive runs raised nothing, because they
  had never concluded anything to go blind from.
- **Two bugs the live run exposed.** The connection check asked the *deployment* provider factory about
  a Hetzner object-storage credential — storage and DNS are deliberately not deployment providers — and
  reported the resulting nothing as "could not reach hetzner-storage", a permanent inconclusive row no
  action would ever clear. Fixed, with a test. And an unauthorized Vercel token made
  `DeploymentVerificationService` throw, collapsing all eight live-URL checks on two projects into one
  coarse `contributor.live_urls` inconclusive; the isolation worked, but the granularity is now its own
  recorded gap.

**What remains**: the sweep records, surfaces and notifies, but still never blocks a deploy — chosen
deliberately, since a false positive would lock a user out of the one action that might fix things.
