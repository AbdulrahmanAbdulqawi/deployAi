using DeployAI.Infrastructure.Adapters;

namespace DeployAI.Tests.Adapters;

public class SsrFrontendDockerfileTests
{
    private const string PackageJsonWithEngines = """
        {
          "name": "web",
          "engines": { "node": ">=22.12.0" },
          "dependencies": { "next": "16.2.10" }
        }
        """;

    [Fact]
    // The whole point of owning this Dockerfile: Coolify's Nixpacks build never passes the app's
    // env to `npm run build`, so a Next.js bundle keeps its localhost fallback. The public keys
    // have to be declared as build args and exported before the build command runs.
    public void Build_DeclaresPublicKeysAsBuildArgsBeforeTheBuildCommand()
    {
        var dockerfile = SsrFrontendDockerfile.Build(
            PackageJsonWithEngines,
            hasLockfile: true,
            buildTimeEnvKeys: ["NEXT_PUBLIC_API_URL"]);

        Assert.Contains("ARG NEXT_PUBLIC_API_URL", dockerfile);
        Assert.Contains("ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL", dockerfile);

        var argIndex = dockerfile.IndexOf("ARG NEXT_PUBLIC_API_URL", StringComparison.Ordinal);
        var buildIndex = dockerfile.IndexOf("RUN npm run build", StringComparison.Ordinal);
        Assert.True(argIndex < buildIndex, "the build arg must be set before the build runs, or it is not inlined");
    }

    [Fact]
    // Secrets and server-only runtime settings must not become build args: a build arg for a
    // browser bundle is public by definition, and runtime values already come from the container.
    public void Build_IgnoresKeysThatAreNotPublicBuildTimeValues()
    {
        var dockerfile = SsrFrontendDockerfile.Build(
            PackageJsonWithEngines,
            hasLockfile: true,
            buildTimeEnvKeys: ["NEXT_PUBLIC_API_URL", "Tickets__SigningKey", "DATABASE_URL"]);

        Assert.Contains("ARG NEXT_PUBLIC_API_URL", dockerfile);
        Assert.DoesNotContain("Tickets__SigningKey", dockerfile);
        Assert.DoesNotContain("DATABASE_URL", dockerfile);
    }

    [Fact]
    // Next 16 refuses to run on older Node, and the mismatch otherwise shows up only as an
    // EBADENGINE warning during install — then fails later in a way that reads as a build error.
    public void Build_UsesTheNodeMajorTheProjectRequires()
    {
        var dockerfile = SsrFrontendDockerfile.Build(PackageJsonWithEngines, hasLockfile: true);

        Assert.Contains("FROM node:22-alpine AS build", dockerfile);
    }

    [Fact]
    public void Build_FallsBackToADefaultNodeMajor_WhenEnginesAreUnspecified()
    {
        var dockerfile = SsrFrontendDockerfile.Build("""{ "name": "web" }""", hasLockfile: true);

        Assert.Contains("FROM node:22-alpine", dockerfile);
    }

    [Fact]
    // npm ci requires a lockfile and hard-fails without one, which would turn a deployable repo
    // into a build error for no reason.
    public void Build_UsesNpmInstall_WhenThereIsNoLockfile()
    {
        var dockerfile = SsrFrontendDockerfile.Build(PackageJsonWithEngines, hasLockfile: false);

        Assert.Contains("RUN npm install", dockerfile);
        Assert.DoesNotContain("npm ci", dockerfile);
    }

    [Fact]
    // A lockfile that has drifted from package.json makes `npm ci` exit 1 — real repos drift, and
    // Nixpacks never showed it because it ran `npm install`. Prefer the locked install, but don't
    // let drift break a deploy that would otherwise succeed.
    public void Build_FallsBackFromNpmCi_WhenTheLockfileHasDrifted()
    {
        var dockerfile = SsrFrontendDockerfile.Build(PackageJsonWithEngines, hasLockfile: true);

        Assert.Contains("RUN npm ci || npm install", dockerfile);
    }

    [Fact]
    // Detection already worked out how this project installs; that beats anything inferred here.
    public void Build_PrefersTheProjectsOwnInstallCommand()
    {
        var dockerfile = SsrFrontendDockerfile.Build(
            PackageJsonWithEngines,
            hasLockfile: true,
            installCommand: "pnpm install --frozen-lockfile");

        Assert.Contains("RUN pnpm install --frozen-lockfile", dockerfile);
        Assert.DoesNotContain("npm ci", dockerfile);
    }

    [Fact]
    // Exec form keeps the Node process as PID 1 so it receives the stop signal directly.
    public void Build_EmitsStartCommandInExecForm()
    {
        var dockerfile = SsrFrontendDockerfile.Build(
            PackageJsonWithEngines,
            hasLockfile: true,
            startCommand: "npm run start");

        Assert.Contains("""CMD ["npm", "run", "start"]""", dockerfile);
    }

    [Fact]
    public void Build_ExposesTheProxyPort()
    {
        var dockerfile = SsrFrontendDockerfile.Build(PackageJsonWithEngines, hasLockfile: true);

        Assert.Contains($"EXPOSE {SsrFrontendDockerfile.ContainerPort}", dockerfile);
        Assert.Contains($"ENV PORT={SsrFrontendDockerfile.ContainerPort}", dockerfile);
    }

    [Theory]
    [InlineData("NEXT_PUBLIC_API_URL", true)]
    [InlineData("VITE_API_BASE", true)]
    [InlineData("PUBLIC_API_URL", true)]
    [InlineData("REACT_APP_API", true)]
    [InlineData("API_URL", false)]
    [InlineData("Cors__Origins__0", false)]
    public void ResolvePublicKeys_KeepsOnlyBundleInlinedPrefixes(string key, bool expected)
    {
        var resolved = SsrFrontendDockerfile.ResolvePublicKeys([key]);

        Assert.Equal(expected, resolved.Contains(key));
    }
}
