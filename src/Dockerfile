FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY DeployAI.Api/DeployAI.Api.csproj DeployAI.Api/
COPY DeployAI.Core/DeployAI.Core.csproj DeployAI.Core/
COPY DeployAI.Data/DeployAI.Data.csproj DeployAI.Data/
COPY DeployAI.Infrastructure/DeployAI.Infrastructure.csproj DeployAI.Infrastructure/
COPY DeployAI.Providers/DeployAI.Providers.csproj DeployAI.Providers/

RUN dotnet restore DeployAI.Api/DeployAI.Api.csproj

COPY . .
RUN dotnet publish DeployAI.Api/DeployAI.Api.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/out .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DeployAI.Api.dll"]
