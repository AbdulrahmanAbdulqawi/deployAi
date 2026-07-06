# Railway GraphQL schema maintenance

DeployAI uses [Strawberry Shake](https://chillicream.com/docs/strawberryshake) to generate a typed Railway client from committed GraphQL operations and schema.

## Project layout

```
src/DeployAI.Providers.Railway.GraphQL/
  schema.graphql          # Railway API schema (committed)
  .graphqlrc.json         # Strawberry Shake codegen config
  Operations/**/*.graphql # One file per operation
```

Generated code is produced at build time under `obj/**/berry/` and is not committed.

## When to refresh the schema

| Trigger | Action |
|---------|--------|
| `dotnet build` fails because an operation references a missing field | Re-download schema, commit, fix operations |
| New Railway API feature needs fields not in the schema | Re-download schema, add fields to the relevant `.graphql` file, rebuild |
| Quarterly hygiene | Diff `schema.graphql`, review deprecated fields |

## Download a fresh schema

Requires a Railway account token (same token used in the Railway dashboard):

```bash
# From repo root — restore the GraphQL CLI tool first
dotnet tool restore

# Set your token (PowerShell)
$env:RAILWAY_TOKEN = "your-account-token"

# Download schema
dotnet graphql download https://backboard.railway.com/graphql/v2 `
  -H "Authorization: Bearer $env:RAILWAY_TOKEN" `
  -f src/DeployAI.Providers.Railway.GraphQL/schema.graphql
```

On bash:

```bash
export RAILWAY_TOKEN="your-account-token"

dotnet graphql download https://backboard.railway.com/graphql/v2 \
  -H "Authorization:Bearer $RAILWAY_TOKEN" \
  -f src/DeployAI.Providers.Railway.GraphQL/schema.graphql
```

After downloading, rebuild to validate all operations against the new schema:

```bash
dotnet build src/DeployAI.Providers.Railway.GraphQL/DeployAI.Providers.Railway.GraphQL.csproj
dotnet test src/DeployAI.Tests/DeployAI.Tests.csproj --filter "FullyQualifiedName~RailwayProviderContractTests"
```

Commit both `schema.graphql` and any operation fixes in the same change.

## Adding a new operation

1. Add `Operations/<Domain>/<OperationName>.graphql` with the query or mutation.
2. Run `dotnet build` on the GraphQL project — codegen validates against the schema at compile time.
3. Call the generated operation from `RailwayProvider` via `_graphQl.CreateSession(credentials)`.
4. Extend `RailwayProviderContractTests` with a MockHttp handler matched by operation name.

Do not add inline GraphQL strings in `DeployAI.Providers/Railway/` — all Railway HTTP calls go through the generated client.

## Scalar mappings

Custom Railway scalars are mapped in `.graphqlrc.json`:

| Scalar | .NET type |
|--------|-----------|
| `DateTime` | `DateTimeOffset` |
| `JSON` | `JsonElement` |
| `EnvironmentVariables` | `Dictionary<string, string?>` (deserializes as JSON string in some responses; parsed via `RailwayGraphQlMapping.ParseVariablesJson`) |
| `EnvironmentConfig` | `string` |

If codegen fails on unknown scalars, add a `Scalars.graphql` extension file or temporarily set `strictSchemaValidation: false` in `.graphqlrc.json`.

## Per-user authentication

Railway tokens are per-user, not app-wide. The provider uses `RailwayGraphQlClientFactory.CreateSession(credentials)` to build a short-lived `IRailwayClient` with `Authorization: Bearer {token}` on each call. Do not register `AddRailwayClient()` as a singleton in DI.
