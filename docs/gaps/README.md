# Known gaps — detail index

`CLAUDE.md`'s "Known gaps" section is a one-line-per-gap index, grouped by theme, each linking
here. This directory holds the full narrative for each one: what broke, what was found, what's
fixed, what's still open. Written in the same shape as `docs/12-repository-scanning.md` —
problem, prior state, current status, worked example — so a reader with no prior context on a
given gap can follow it standalone.

| Doc | Covers |
|---|---|
| [provisioning-and-env-vars.md](provisioning-and-env-vars.md) | Duplicate env-var repair, batched upserts, the project-wide store (closed), CORS key guessing, unrecoverable generated secrets |
| [compose-deployments.md](compose-deployments.md) | Coolify proxy labels/domains, compose plans collapsing to a lone frontend, duplicate compose generators, the connection-string and domain-assignment fixes (both closed) |
| [runtime-diagnostics.md](runtime-diagnostics.md) | `runtime-logs`'s two limits: unavailable for a stopped container, and — for a compose app — only ever one container's output |
| [verification-and-config-checks.md](verification-and-config-checks.md) | Required-config checks that warn but don't block, the fleet sweep that closed shallow verification and provider revalidation, and what is still open: no divergence/migration-chain checks, no test-coverage gate |
| [database-provisioning.md](database-provisioning.md) | Can't create a Coolify project, only .NET gets schema applied, a provisioned Coolify database nothing outside it can reach |
| [object-storage.md](object-storage.md) | The file-storage layer hand-written per app, and the account-wide credentials handed to every app |
| [domains-and-certificates.md](domains-and-certificates.md) | The DNS-before-certificate gate, conclusive vs inconclusive checks, the live-domain re-check and the enum-as-string convention (both closed), and what is still open: the single-server wildcard, and a TLS failure still reported as "redeploy the server" |
| [process.md](process.md) | Generated commit messages (closed), the untested-change gap, and top-level docs that drifted from the shipped product |

For the repository-scanning class of gap specifically (which directory in a repo is the app),
see `docs/12-repository-scanning.md` directly — it already carries its own "What did not move"
section rather than duplicating it here.
