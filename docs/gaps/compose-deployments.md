# Compose deployments

**Status:** the shape that keeps recurring — a compose target standing for two roles (website +
server) in one persisted record, and Coolify's own compose-specific behavior being different
enough from a single-app deploy that code written for one silently mishandles the other. Two
instances closed 2026-07-31/08-01 while deploying Mirqab end to end; three still open.

## Coolify's proxy labels are managed by Coolify, and DeployAI must not write them

Writing `custom_labels` at all is what left two apps unroutable; the field is no longer sent.
The stale-label problem is still open and has a worked example. Mirqab's app carried a *custom*
label set pinned to `loadbalancer.server.port=80` from the day it was created. Every later
correction — build pack, Node version, port — reached Coolify and none reached the proxy, so the
container ran, the deploy reported success, and every request got 502 from Traefik while nginx
logged no requests at all.

**An empty Labels box is the healthy state**: Coolify regenerates labels from `ports_exposes` on
each deploy, and the app that works has nothing in that box. A custom set is what freezes the
port. Recovery took Coolify's own "Reset Labels to Defaults" *and* a redeploy — one deploy after
the reset was not enough, and the labels stayed empty until the deploy after that.

What DeployAI could do without writing labels itself: notice that the port it just set differs
from the one the proxy is using and say so, rather than reporting a green deploy for an
unreachable site.

## A compose deploy is announced, then quietly reduced to its frontend — closed for the silence, open for the automation

A single-origin compose plan has a website part and a server part that deploy as one Coolify
application, so only one target is created — and nothing recorded that the target *was* a
compose deployment. Every later reader re-derived the shape from a lone Angular website:
`PlanUsesSingleOriginCompose` needs both halves so it answered false, readiness therefore
skipped the compose rules and reported a repository with no `docker-compose.coolify.yml` as
ready, and `SsrWebsiteBuildProvisioner` matched on the website role and switched the application
off Docker Compose onto a Dockerfile build of `client/`. The wizard said "site, API and
database"; what deployed was an Angular bundle, and nothing named the difference.

The target now carries `composeFileLocation`, `composeServerDirectory` and
`composeServerFramework`; the provisioner refuses to touch a compose target; and
`DeployTargetPlanParts` expands the one target back into the two parts it stands for, so every
existing compose check works unchanged.

**The shape, again: decided correctly, persisted incompletely, re-derived from worse information
by the last writer** — the same one behind the stale ConfigJson and the port that fell back to
8080.

**What remains**: the compose file is still produced only by the user pressing "Set up
deployment files", which opens a PR they must merge. That is now *reported* — the plan card
lists the missing file and the deploy refuses with its name — but a plan DeployAI proposed still
cannot be published without a manual step, which by the core rule is a gap and not a workflow.

## Two unfinished implementations of compose generation exist side by side

The one that runs is template-based (`DeploymentTemplates/single-origin-compose/angular-dotnet-coolify`,
six `.tpl` files) and is keyed to exactly one stack: `SingleOriginComposeShape` hardcodes Angular
+ .NET on Coolify. The other is a capability graph — `DeploymentGraph`, `ServiceNode`,
`SingleOriginTransform`, `ResourceWiringTransform`, `ComposeGenerator`, `CoolifyComposePlanner`,
~750 lines — written explicitly to replace that hardcoded gate ("this class contains no
framework names") and referenced by nothing outside its own tests.

Neither is wrong; having both is. The graph is the better answer and the missing link is small —
nothing builds a `DeploymentGraph` from a classification — but until one of them is chosen, a
React + Express repo gets no compose plan at all while the code that would have handled it sits
complete and unreachable.

## Connection-string keys declared only in a compose environment block were never read — closed

`DatabaseRequirementDetector` read `docker-compose.yml` for the postgres/redis *image* (to decide
whether a database is needed at all), but never for the *key name* the app actually asks for. A
compose environment line like `ConnectionStrings__Postgres: "Host=postgres;..."` — the standard
double-underscore env-var form of the config path `ConnectionStrings:Postgres` — was invisible to
detection, so provisioning wired only the generic defaults (`ConnectionStrings__Default`,
`DATABASE_URL`) and the app's own container kept crash-looping on
`ConnectionStrings:Postgres is not configured`, even after the database existed and was reachable.

Found deploying Mirqab, whose `appsettings.json` declares no `ConnectionStrings` section at all —
`Program.cs` reads `Configuration.GetConnectionString("Postgres")` purely from code, with the key
name declared nowhere DeployAI previously looked except the compose file it was already reading
for the image check.

Fixed by teaching `DatabaseRequirementDetector.DetectConnectionStringsFromComposeEnvironment` to
read the same compose content for `ConnectionStrings__X` keys, merging them into the same
`ConnectionStringKeys` list `appsettings.json` scanning already produced. Verified live: re-running
detection against Mirqab returned `connectionStringKeys: ["Postgres"]`, provisioning wired
`ConnectionStrings__Postgres` onto the running app, and the next deploy's log read "This app
logged no errors of its own while starting" where it had previously crash-looped on that exact
line. (`e6e181e`)

## A compose app never received a domain, so its proxy route never existed — closed

Distinct from the stale-labels gap above: this is not a label freeze, it is the domain never
being assigned in the first place. `CoolifyProvider.CreateApplicationAsync` skips domain
assignment entirely for a compose app (`!isCompose` guard) because Coolify rejects
`docker_compose_domains` before the first deploy has parsed the compose file — but nothing ever
came back afterward to assign one. The app's top-level `fqdn` stayed `null` forever, and
Traefik routes a compose app off `docker_compose_domains`, never `fqdn`, so the site returned a
flat `404 page not found` — Traefik's own not-found response — even though both containers had
started cleanly.

The URL DeployAI showed in its own UI (`{uuid}.{server-ip}.sslip.io`) was not proof otherwise: it
came from a `TryResolveApplicationUrlAsync` call that reads `application.Fqdn`, which was empty —
so the value shown had actually resolved to `null` and gone unnoticed downstream.

Fixed with `IComposeDomainAssignment.AssignComposeDomainAsync`, called after every successful
compose deploy of the website role. Passing `domain: null` lets `CoolifyProvider` derive
Coolify's own sslip.io convention (`{uuid}.{server-ip}.sslip.io`) from the connection's own
instance address, rather than depending on `fqdn` ever being populated — the chicken-and-egg that
made a first attempt at this fix silently never fire. Idempotent, so it runs on every deploy
rather than once.

**Two deploys, not one, to see it take effect** — the same lag documented in the stale-labels
gap above, and worth remembering as a general Coolify behavior, not a Mirqab quirk: deploy N
attaches `docker_compose_domains`, but that deploy's own Traefik labels were already generated
before the domain existed; deploy N+1 is the one that actually routes. Verified live against
Mirqab: deploy 1 attached the domain, deploy 2 came up with Traefik correctly serving the app —
the site went from a flat 404 to rendering its full Angular login page, all assets 200. (`5967a81`)

## Non-goals

- **Not a fix for a hand-written compose file's own env-var conventions.** A `docker-compose.coolify.yml`
  that intentionally omits mapping flat provider-UI variable names onto the nested config keys an
  app reads (deferring entirely to "set the real keys directly in the provider's UI," as Mirqab's
  own file documented) is an app-level authoring choice, not a DeployAI detection gap — see the
  `diagnose-coolify-deploy` skill for how to recognize this shape when a container is unhealthy
  despite "correct-looking" environment variables.
