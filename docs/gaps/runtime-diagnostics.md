# Runtime diagnostics

**Status:** two distinct limits on the same capability (`runtime-logs`), both open. Neither is a
DeployAI bug in the sense of "wrong code" — both are the Coolify API's own shape — but both mean
DeployAI (and anyone using it to debug) can be looking at the wrong evidence without knowing it.

## Runtime logs are unavailable exactly when they are needed

`runtime-logs` returns "Application is not running" for a stopped container, so the capability
added for "an app that builds fine but crash-loops" cannot read the crash. It took a
`lifecycle/start` first, and a container that has hit Coolify's restart limit stays stopped
until something starts it. For a *running* container it works well — it is what diagnosed a
`/public/stats` 500, returning the full EF translation error and the SQL around it in one call.

## Coolify's logs API only ever returns one container, with no way to choose another

For a Docker Compose application with multiple services, `GetRuntimeLogsAsync` (`GET
applications/{uuid}/logs`) always returns exactly one container's output — whichever Coolify's
own container-status query lists first — and there is no query parameter to request a different
one.

**Confirmed at the source, not guessed**: Coolify's own controller
(`ApplicationsController::logs_by_uuid` in `coollabsio/coolify`) does
`$container = $containers->first();` with no `container_name` or `service` parameter accepted
anywhere in the method. This is a hard limit of the API DeployAI calls, not a bug in how
DeployAI calls it.

Found diagnosing Mirqab's `api`/`web` compose pair: `runtime-logs` consistently returned nginx's
(`web`'s) entrypoint log, never the `.NET` `api` container's — including at points where `api`
was the one crash-looping. `RuntimeExceptionCheck`, which runs this same endpoint before and
after every deploy to catch startup crashes, inherits the same blind spot for any compose app
whose non-primary service is the one failing: it can report "this app logged no errors of its
own while starting" while genuinely only having read the healthy container.

**Fallback that sometimes works**: Coolify's own web UI lets a human pick a specific container
via a dropdown on the Logs and Terminal tabs — but both of those depend on a websocket/broadcasting
channel that may not be reachable from a sandboxed browser session ("Cannot connect to real-time
service"). When that channel is down, a Livewire component's *current* server-rendered state is
still readable directly from its `wire:snapshot` DOM attribute — but that snapshot does not carry
streaming log content, so it can confirm configuration (e.g. `parsedServiceDomains.web.domain`)
without ever substituting for the log stream itself. See the `diagnose-coolify-deploy` skill for
the concrete steps.

**What would close this**: nothing on DeployAI's side can add a parameter Coolify's API doesn't
accept. The only real fix is upstream (a Coolify feature request) or a different diagnostic path
entirely — e.g. having the generated Dockerfile/entrypoint write each service's own log to a
distinguishable, separately-fetchable location, which is speculative and not attempted.
