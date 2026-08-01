---
name: diagnose-coolify-deploy
description: Diagnose a broken, unhealthy, or unreachable Coolify deployment — 502s, a container restarting, a site returning 404/blank, "the deploy isn't working." Encodes Coolify API/UI limitations already discovered the hard way, so they don't need rediscovering every session.
---

# Diagnosing a broken Coolify deploy

Follow this order. Each step exists because a shortcut past it produced a wrong answer at least
once. See `docs/gaps/runtime-diagnostics.md` and `docs/gaps/compose-deployments.md` for the full
incident narratives behind these steps.

## 0. Check the application's overall status before anything else

In Coolify's own Configuration tab, the status line right under the app name (`Running`,
`Restarting`, `Exited`) rules out or confirms the simplest explanation before you go looking for
a routing or logs problem. **A container that keeps crash-looping past Coolify's restart limit
(default 10) stops entirely** — `Exited`, "Stopped after reaching restart limit (N/10)" — and a
stopped app 404s at its domain for a completely different reason than a missing route: there's
nothing listening at all, however correctly the domain and labels are configured. Don't
diagnose routing (steps 3, 5 below) against an app in this state — restart it
(`lifecycle/start`, or Coolify's own Actions menu) and see whether it crash-loops again first;
if it does, the fix is whatever's causing the crash (step 4), not the route.

## 1. Establish ground truth from DeployAI first

Per `CLAUDE.md`'s standing rule, diagnose from the deployed artifact, not the local repo:
- `GET /api/projects/{id}/services/{targetId}/status` — current provider-reported status.
- `GET /api/deployments/{id}` and `.../logs` — the actual build/deploy log, including whatever
  `RuntimeExceptionCheck` found before and after the build.
- `GET /api/projects/{id}/services/{targetId}/runtime-logs` — container stdout.

**Know its limit before trusting it**: for a Docker Compose app, this endpoint (and Coolify's
own `GET applications/{uuid}/logs` API underneath it) always returns exactly one container's
output — whichever Coolify's own container-status query lists first, with no parameter to
choose another. If the app has multiple services (e.g. `api` + `web`), this may be showing you
the *healthy* one while the *other* one is what's actually crash-looping. Don't conclude "no
errors" from this alone for a multi-container app — it may simply never have read the container
that's failing.

## 2. For a compose app, go straight to Coolify's own UI

Navigate to `{coolify-url}/project/{project}/environment/{env}/application/{uuid}` directly (get
these UUIDs from the deploy target's `providerProjectId` / the project's stored connection).
The Logs and Terminal tabs both have a container-selector dropdown DeployAI's own API doesn't
expose — use it to pick the specific service you actually need.

**If the live log stream or terminal shows "No logs yet" / never connects** ("Cannot connect to
real-time service"): that's a websocket/broadcasting channel that may not reach a sandboxed
browser session. Don't conclude "the container has no output" — try the fallback below instead
of giving up.

**Fallback — read Livewire's server-rendered state directly**, even with the live channel down:
```js
Array.from(document.querySelectorAll('[wire\\:snapshot]')).map(el => {
  try { return JSON.parse(el.getAttribute('wire:snapshot')); } catch { return null; }
});
```
This confirms *current configuration* (e.g. `parsedServiceDomains.web.domain`, the rendered
`docker_compose_raw`, container names/restart counts) — it does not carry streaming log
content, so it can't substitute for step 1's actual log read, only supplement it.

## 3. If a compose app 404s at its own domain

Check `parsedServiceDomains.{service}.domain` in Coolify's Configuration tab (or via the
`wire:snapshot` extraction above) for the browser-facing service (usually `web`). Empty means
Traefik has no route at all — Coolify routes a compose app off `docker_compose_domains`, never
its top-level `fqdn`, so a populated-looking URL shown in DeployAI's own UI is not proof the
route exists.

**Remember the two-deploy lag**: assigning a domain takes effect on the *next* deploy, not the
one that assigned it — that deploy's Traefik labels were already generated before the domain
existed. If you just fixed a missing-domain assignment, redeploy once more before concluding it
didn't work.

## 4. If a container is unhealthy/502ing despite "correct" env vars

Check whether a hand-written `docker-compose.coolify.yml` actually performs the same
`${VAR}` → nested-config-key substitution its local dev `docker-compose.yml` does. A compose
file that defers entirely to "set the real keys directly in the provider's UI" (a legitimate,
documented pattern) needs the *nested* keys (e.g. `Section__Key`) wired on the target — not the
flat placeholder names from the local file's right-hand `${...}` side, which mean nothing to a
compose file that never substitutes them. Compare the two compose files side by side before
assuming the env vars shown in DeployAI's UI are the ones the app actually reads.

## 5. Stale Traefik labels (single-app or compose)

An empty Labels box in Coolify's Configuration tab is the *healthy* state — Coolify regenerates
labels from `ports_exposes` on every deploy. A populated/custom label set freezes whatever port
was true when it was written, and DeployAI must never write to this field itself. Recovery is
Coolify's own "Reset Labels to Defaults" *and* a redeploy — one deploy after the reset is not
enough; the labels stay empty until the deploy after that.
