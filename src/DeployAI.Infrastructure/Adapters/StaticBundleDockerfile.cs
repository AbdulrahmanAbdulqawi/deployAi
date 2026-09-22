namespace DeployAI.Infrastructure.Adapters;

/// <summary>
/// The Dockerfile that turns a built front-end bundle into an nginx image.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every adapter whose framework compiles to static files, because the image is the
/// same one in each case — only the build command and the directory the build writes to differ.
/// Two copies would be two places to fix the day the proxy config moves, and the failure would be
/// silent in whichever copy was forgotten: an image that serves the bundle and drops the /api
/// route is a site that loads and cannot log in.
/// </para>
/// <para>
/// The final stage is nginx rather than a static-file build pack on purpose. A build pack sees
/// none of the app's environment, and a build-time API base would simply be absent; and a wrong
/// output directory fails loudly at <c>COPY</c> here instead of publishing a blank site.
/// </para>
/// </remarks>
internal static class StaticBundleDockerfile
{
    /// <summary>What nginx listens on, and what the deployment's proxy routes the domain to.</summary>
    internal const int ExposedPort = 80;

    internal static string Render(string directory, string buildCommand, string outputDirectory) =>
        $"""
        # Built with ./{directory} as the context; COPY paths are relative to it.
        FROM node:22-alpine AS build
        WORKDIR /src
        COPY package*.json ./
        RUN npm ci
        COPY . .
        RUN {buildCommand}

        FROM nginx:alpine
        COPY nginx.conf /etc/nginx/conf.d/default.conf
        COPY --from=build /src/{outputDirectory} /usr/share/nginx/html
        EXPOSE {ExposedPort}
        """;
}
