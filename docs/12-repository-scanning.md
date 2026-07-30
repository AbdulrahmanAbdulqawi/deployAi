# Repository scanning — one way to find an app inside a repo

**Status:** proposed, not started. Written 2026-07-29 after the fourth instance in one day.

## The problem, stated once

DeployAI answers the same question in at least six places: *given a repository and a deploy
target, where do this app's files live?* Every one of them answers it separately, and every one
of them answers it by assuming a layout.

When the assumption is wrong the scan returns nothing. Nothing is not an error — it is
indistinguishable from "this repo genuinely has no such file" — so the deploy proceeds, the
capability that depended on the scan quietly does not run, and the failure surfaces later
somewhere that points nowhere near the cause.

This is not a hypothetical. In a single day of deploying one monorepo:

| # | What silently did nothing | Because it read | The app's files are at |
|---|---|---|---|
| 1 | `env-schema` returned zero variables | repo root only | `backend/src/YemenHub.Api/appsettings.json` |
| 2 | Server Dockerfile never regenerated | `backend/src` (no csproj there) | `backend/src/YemenHub.Api/*.csproj` |
| 3 | Object storage never provisioned | `backend/src` (no appsettings there) | `backend/src/YemenHub.Api/appsettings.json` |
| 4 | Nothing checked config the merged code needed | — | `backend/src/YemenHub.Api/appsettings.json` |

Numbers 2 and 3 were each fixed by adding a bespoke "look one level down" to that one caller.
That is the third and fourth patch to the same bug. The fifth is already implied: nothing has
been done for number 1, and number 4 has not been built.

## What exists today

Six independent scanners, each with its own idea of where to look:

| Caller | Reads | Depth |
|---|---|---|
| `EnvVarDetector` via `env-schema` | `docker-compose.yml`, `.env.example`, `README.md`, `appsettings.json` | repo root, plus `appsettings.json` under an explicitly supplied `serverPath` |
| `RepositoryClassifier` | `docker-compose.y[a]ml`, `appsettings.json` | repo root |
| `ServerBuildProfileDiscovery` | `package.json`, `requirements.txt`, `pyproject.toml`, `Cargo.toml` | supplied directory |
| `ServerDockerfileProvisioner` | `*.csproj` | service dir **+ one level** (patched) |
| `SsrWebsiteBuildProvisioner` | `package.json`, `package-lock.json` | app dir only |
| `ObjectStorageAutoProvisioner` | `appsettings.json`, manifests, compose | service dir **+ one level** (patched) |
| `RailwayDatabaseProvisioningService` | `docker-compose.y[a]ml`, `appsettings.json` | repo root |

Two of the seven know about nesting. Five do not. None of them share a line of code, so fixing
one teaches the others nothing — which is exactly why this keeps recurring.

## Why patching each caller does not work

Each caller asks a slightly different question ("is there a csproj?", "is there a package.json?",
"what is in appsettings?") but they all depend on the same prior fact: **which directory is this
app**. That fact is derived independently, badly, seven times.

It is also the fact most likely to be wrong, because it is the one thing that varies between
repositories. Everything downstream — which framework, which port, which config keys — is
reliable once the directory is right.

## Proposed shape

One component resolves the app's directory, once, and every scanner reads through it.

```csharp
/// Where an app actually lives inside a repository, resolved once per deploy.
public sealed record RepositoryLayout(
    string RepoRoot,          // ""
    string BuildRoot,         // the Docker build context, e.g. "backend/src"
    string ProjectDirectory,  // where the app's own files are, e.g. "backend/src/YemenHub.Api"
    IReadOnlyList<string> SearchPath);  // ProjectDirectory, BuildRoot, RepoRoot — nearest first

public interface IRepositoryLayoutResolver
{
    /// Resolves by evidence, not assumption: the directory holding the entry project
    /// (Microsoft.NET.Sdk.Web csproj / package.json with a start script / manage.py / go.mod).
    Task<RepositoryLayout> ResolveAsync(
        string token, string owner, string repo, string branch,
        string? configuredDirectory, CancellationToken ct);
}

public interface IRepositoryReader
{
    /// The nearest match along the search path, with the path it came from.
    Task<RepositoryFile?> FindAsync(RepositoryLayout layout, string fileName, CancellationToken ct);

    /// Every match, for scanners that want them all (several csproj, several package.json).
    Task<IReadOnlyList<RepositoryFile>> FindAllAsync(RepositoryLayout layout, string glob, CancellationToken ct);
}

public sealed record RepositoryFile(string Path, string Content);
```

Two properties matter more than the shape:

**It reports what it read.** `EnvScanResult` already carries `SourcesRead` / `SourcesMissing` and an
`IsInconclusive` flag, for exactly the reason described above. That idea belongs here, at the
bottom, so every scanner inherits it instead of each one reinventing it. A caller must always be
able to distinguish "no bucket keys in this repo" from "I could not find this app's files."

**It resolves by evidence.** `ServerDockerfileProvisioner.FindWebProjectOneLevelDownAsync` already
does the right thing — it picks the `Microsoft.NET.Sdk.Web` project rather than the first csproj it
sees, because building `YemenHub.Modules` would produce an image with no entry point. That logic is
the seed of the resolver and should move into it.

## Order of work

1. **Resolver + reader, with tests, used by nothing.** Cover the layouts we have actually hit:
   root-level app; `client/` + `backend/src/<Project>`; a compose repo with several services.
2. **Move `ObjectStorageAutoProvisioner` and `ServerDockerfileProvisioner` onto it** and delete
   their bespoke descent. These two already pass — if the shared component cannot keep them
   passing, it is the wrong shape.
3. **Move `EnvVarDetector`'s inputs onto it.** This is the one with a live incident behind it: an
   API reached production and crash-looped on `Jwt configuration missing` because the scan read the
   root and the config was three levels down.
4. **Then build the pre-deploy config check** (see below), which is only worth building on top of a
   scanner that can find the files.

Steps 1–3 should not change behaviour for any repo that already works. Step 3 will change it for
nested repos — that is the point — so it wants a deliberate look at what the wizard then shows.

## What this unlocks

The check that would have prevented the outage on 2026-07-28: **compare the configuration the
deployed ref requires against what the target actually has.** Merging the feed branch brought a
Media module needing `Storage:*`; nothing was set; `AmazonS3Client` threw before `builder.Build()`;
every route died and Coolify gave up after eleven restarts. The migration chain was validated and
the build was green — neither could have caught it.

That check needs two things DeployAI now has separately: the ability to read an app's real
configuration files (this document), and the ability to read what a target actually has (the
environment listing, added 2026-07-29). It is a short piece of work once the first exists, and
it turns a class of silent production failure into a line in the deploy log.

## Non-goals

- **Not a full repository crawl.** Depth is bounded and paid on every deploy. Nearest-first along a
  short search path, not a tree walk.
- **Not a replacement for explicit configuration.** When a user has told DeployAI the service
  directory, that wins. The resolver fills in what they did not say.
- **Not framework detection.** `RepositoryClassifier` and `ServerBuildProfileDiscovery` keep their
  jobs; they just stop guessing where to look.
