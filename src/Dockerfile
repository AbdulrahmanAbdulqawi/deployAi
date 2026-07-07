FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /app



COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/

COPY DeployAI.Core/DeployAI.Core.csproj DeployAI.Core/

COPY DeployAI.Data/DeployAI.Data.csproj DeployAI.Data/

COPY DeployAI.Infrastructure/DeployAI.Infrastructure.csproj DeployAI.Infrastructure/

COPY DeployAI.Providers/DeployAI.Providers.csproj DeployAI.Providers/

COPY DeployAI.Providers.Railway.GraphQL/DeployAI.Providers.Railway.GraphQL.csproj DeployAI.Providers.Railway.GraphQL/



RUN dotnet restore DeployAI.Api/DeployAI.Api.csproj



COPY . .

RUN dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out



FROM mcr.microsoft.com/dotnet/sdk:8.0

WORKDIR /app



RUN apt-get update \

    && apt-get install -y --no-install-recommends curl ca-certificates gnupg \

    && mkdir -p /etc/apt/keyrings \

    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \

    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_20.x nodistro main" > /etc/apt/sources.list.d/nodesource.list \

    && apt-get update \

    && apt-get install -y --no-install-recommends nodejs \

    && apt-get clean \

    && rm -rf /var/lib/apt/lists/*



COPY --from=build /app/out .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "DeployAI.Api.dll"]

