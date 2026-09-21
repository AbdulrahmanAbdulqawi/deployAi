# Provisioning and environment variables

**Status:** partially closed, two open items below.

## Duplicate repair only ran on the database-linking path — closed

The write race is fixed — `CoolifyProvider.UpsertEnvVarAsync` goes through Coolify's
`PATCH /envs/bulk`, which resolves by key server-side, instead of the old non-atomic
list-then-create. Repair exists too: `ReconcileDuplicateEnvVarsAsync` deletes every record after
the first for a key, which is the only safe rule because Coolify's bulk handler resolves with
`->where('key', $key)->first()` and so writes to the first record and leaves later ones stale.

It was at first wired only into `LinkDatabaseVariablesAsync`, so an application that never got a
database link was never repaired. `511db92` ("Repair duplicate env-var records on every deploy,
not just when linking a database", 2026-07-30) moved the call into `DeploymentOrchestrator.RunAsync`,
where it runs for every target before every deploy. The observation that motivated it — one app
with 32 records for 16 keys, two `DATABASE_URL`s pointing at *different* Postgres instances, one of
which no longer existed — stands as the reason the rule is "keep the first".

This entry advertised the gap as open for seven weeks after that commit (found 2026-09-21 while
inventorying the Coolify instance). Second time in a day a gaps entry had outlived its gap; see
`verification-and-config-checks.md` for the first, and the same note about nothing detecting it.

## Callers still upsert one key at a time

`UpsertEnvVarsAsync` can apply a whole set in a single request, but
`FrontendEnvironmentWiringService` and `CoolifyProvider.Database` still loop key by key.
Batching would cut N round trips to one and leave no window for a concurrent sync to
interleave mid-set.

## A brand-new application is born with duplicate variables

The repair described above works. What it also does is hide the thing that makes it necessary.

On 2026-09-22 a compose application was created from scratch by the wizard — project created,
application created, environment values collected, first deploy triggered — and the very first
line of its deploy log read:

> Removed 14 duplicate environment variable record(s) on this app. Duplicates make the value an
> app reads differ from the one shown.

Fourteen duplicates on an application that had existed for under a minute and had never been
deployed. Every variable had been written twice. `PATCH /envs/bulk` resolves by key, so two
sequential writes of the same set cannot produce this; either two writers race, or one of them
is not going through the bulk path. The wizard's env step and the pre-deploy wiring
(`FrontendEnvironmentWiringService`) both write the same keys, and the per-key looping recorded
above is the window that makes interleaving possible.

Nobody noticed for as long as this has been happening because the reconciler cleans it up and
reports a tidy number. A repair that runs on every deploy is exactly the thing that lets a
duplicate-producing write path stay invisible — the log line reads like maintenance rather than
like a defect report.

**The reflected fix is one write path, not a better repair:** find the second writer, batch both
callers through `UpsertEnvVarsAsync`, and treat a non-zero reconcile count on a
*newly created* application as an error rather than a statistic.

## The managed environment store was project-wide — closed

`ProjectEnvironmentStore` now keys by target as well as name, so a website and a server that
both carry `API_URL` no longer share one record — saving on one used to overwrite the other's
value and deleting from one erased both. The old flat blob still reads: those entries land in
a bucket belonging to no app, are still exported, and move to a real target the first time one
is saved or deleted there, so an existing project converges without a migration that would
have to guess.

**What remains** is upstream of the store: a project whose only deployable target is a website
has nowhere else for a server secret to go, which is how four `MIRQAB_*` secrets ended up on a
frontend and were then baked into its image as build args by Coolify's Nixpacks builder. The
store no longer confuses them; nothing yet warns that a secret is on the wrong kind of app.

## CORS wiring is a guess, and nothing checks whether the guess was right

`ResolveServerCorsEnvKeys` writes a fixed list of key names per framework. It now includes
ASP.NET's own `Cors__Origins__0` / `Cors__AllowedOrigins__0` alongside DeployAI's `App__*`
convention, but it is still a list of names hoped to match. An app reading any other key gets
its origins written nowhere it looks, keeps whatever hardcoded fallback its source has, and
nothing reports it: the API logs nothing (the browser never sends the request), both deploy
targets go green, `/health` passes, and the only symptom is the frontend's own "cannot reach
the server" message.

One `OPTIONS` from the website origin to the API, checking for `Access-Control-Allow-Origin`,
would settle it in a single request at the end of a deploy — that is the fix; the key list is a
stopgap. Better still: read the key out of the repository (`GetSection("...")` in `Program.cs`)
rather than guessing names at all, which `RepositoryLayoutResolver` (`docs/12-repository-scanning.md`)
now makes possible.

**The pattern to copy already exists**: `ObjectStorageVerifier` sends exactly that preflight
against the bucket and reports the result on every deploy. Doing the same from the website
origin to the API is the same shape of work.

## Secrets DeployAI generates exist only in DeployAI

`Jwt__SigningKey`, `Tickets__SigningKey` and the storage keys are written to the provider as
secrets, which Coolify will not return, and stored encrypted in DeployAI's own database. If
that database is lost or reset — which happened once already — the values are unrecoverable,
every issued token and ticket signature breaks, and there is no export path. Generating secrets
on a user's behalf implies keeping them recoverable.
