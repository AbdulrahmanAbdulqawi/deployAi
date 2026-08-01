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
