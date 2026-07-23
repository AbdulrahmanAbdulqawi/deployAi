# Built with ./{{serverRoot}} as the context, so every COPY below is relative to that
# directory — not to the repository root. If the project references sibling projects outside
# this directory, move the compose build context up and adjust these paths to match.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# 8080 is the ASP.NET Core default in containers, and it is the port nginx proxies to.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "{{serverAssemblyName}}.dll"]
