---
name: verify-deploy
description: Verify a deploy actually works end to end, not just that it reports success. Use after any fix that touches deployment, provisioning, routing, or env-var wiring, or when asked to confirm a deploy is "actually working."
---

# Verifying a deploy end to end

Operationalizes `CLAUDE.md`'s standing rule: **"Verification must exercise real usage. A
`/health` 200 proves a process is listening, not that the app works."** A green build, a passing
test suite, and a `success` deployment status are each necessary and none of them sufficient —
two real fixes in this codebase (compose connection-string wiring, compose domain assignment)
both looked complete by every one of those signals and were still broken until checked live.

## Steps

1. **Get the real deployed URL** from the project/deployment record, not from memory or an
   assumption — for a compose app in particular, confirm the domain is actually assigned (see
   the `diagnose-coolify-deploy` skill) before treating a URL as reachable.
2. **Navigate to it in the browser tool.** Read the page title and body text. Confirm it's the
   expected app — not a 404, not a 502, not a blank page, not a generic proxy error page. A tab
   title that suddenly makes sense (e.g. changing from nothing to the app's actual name) is a
   strong positive signal; don't skip past it.
3. **Read the network requests panel — not just for 200s on static assets.** Static assets
   (JS/CSS/fonts) succeeding proves the web server is up; it says nothing about the backend.
   Look specifically for the app's *own* API calls (e.g. `/api/auth/me`, `/api/health`) and
   check their actual status — a `502`/`500` here alongside all-green static assets is exactly
   the shape of a backend that's up-as-a-process but broken underneath.
4. **For a split-origin or compose deploy, check both halves independently.** A working
   frontend proves nothing about the API behind it, and vice versa.
5. **Check the console for JS errors** if the page renders but seems wrong.
6. **State plainly what wasn't checked.** If there's no way to test a login flow without real
   credentials, say so explicitly rather than implying full coverage — "the page loads and the
   API responds to health checks; login itself wasn't tested, no credentials available" is an
   honest report. Never claim something "works" when only part of it was actually exercised.

## What this replaces as sufficient evidence (it is not)

- `dotnet test` / the CI suite passing — proves the logic, not the wiring.
- The deployment record's `status: success` — proves the build/deploy pipeline completed, not
  that the running container serves correct responses.
- A `/health` endpoint returning 200 — proves a process is listening on a port, nothing more.
