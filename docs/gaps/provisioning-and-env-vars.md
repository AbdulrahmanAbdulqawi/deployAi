# Provisioning and environment variables

**Status:** partially closed, three open items below.

## Duplicate repair only runs on the database-linking path

The write race is fixed — `CoolifyProvider.UpsertEnvVarAsync` now goes through Coolify's
`PATCH /envs/bulk`, which resolves by key server-side, instead of the old non-atomic
list-then-create. Repair now exists too: `ReconcileDuplicateEnvVarsAsync` deletes every record
after the first for a key, which is the only safe rule because Coolify's bulk handler resolves
with `->where('key', $key)->first()` and so writes to the first record and leaves later ones
stale.

**But it is only wired into `LinkDatabaseVariablesAsync`.** An application that carries
duplicates and never gets a database link is still never repaired, and there is no way to ask
for a repair without deploying. One app was observed with 32 records for 16 keys, including two
`DATABASE_URL`s pointing at *different* Postgres instances — and the stale copies pointed at a
database that no longer existed at all.

## Callers still upsert one key at a time

`UpsertEnvVarsAsync` can apply a whole set in a single request, but
`FrontendEnvironmentWiringService` and `CoolifyProvider.Database` still loop key by key.
Batching would cut N round trips to one and leave no window for a concurrent sync to
interleave mid-set.

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
