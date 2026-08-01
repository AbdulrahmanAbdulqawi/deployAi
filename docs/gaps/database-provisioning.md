# Database provisioning

**Status:** all three open.

## DeployAI cannot create a Coolify project, only deploy into an existing one

A missing environment is now created rather than dead-ending, but the project picker still only
offers projects that already exist. Coolify exposes `POST /projects`, so nothing prevents
closing this — until then, the first deploy into a new Coolify instance is a manual step.

## Only .NET apps get their schema created

The generated .NET Dockerfile now bundles EF migrations and applies them before the app starts.
Nothing equivalent exists for the other runtimes DeployAI deploys — a Node or Python service
provisioned a database still meets an empty one, and the failure looks like a healthy app
returning 500s.

## DeployAI provisions a Coolify database it then cannot reach

`IProviderDataServiceInspection` is implemented by `RailwayProvider` only, so `data-info`
answers `unsupported_provider` for every Coolify database — which is the default. The
connection string DeployAI writes onto the app uses Coolify's internal Docker hostname,
reachable only from inside that network, so nothing outside the container can look at the data:
not the tables panel, not a migration check, not the user.

Found while trying to remove three throwaway accounts a test had created; the only routes to
them are SSH onto the Hetzner host or an endpoint in the app itself, and the first is precisely
what the core rule says must not become routine. A provisioned database nobody can inspect is
also why "did the migrations apply" still has no answer for the provider DeployAI defaults to.
