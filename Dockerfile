# Build with the .NET 10 SDK but publish a net8.0 app: the SDK pulls the net8.0 targeting pack, so
# the assemblies are exactly what the assignment asks for.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first, so a source-only change does not invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/PlayerService.Abstractions/PlayerService.Abstractions.csproj src/PlayerService.Abstractions/
COPY src/PlayerService.Grains/PlayerService.Grains.csproj src/PlayerService.Grains/
COPY src/PlayerService.Api/PlayerService.Api.csproj src/PlayerService.Api/

# The API project and its two project references - the test project is deliberately not restored.
RUN dotnet restore src/PlayerService.Api/PlayerService.Api.csproj

COPY src/ src/
RUN dotnet publish src/PlayerService.Api/PlayerService.Api.csproj \
    --configuration Release --no-restore --output /app

# The real .NET 8 runtime, which is the point of containerising: RollForward=LatestMajor exists only
# because no .NET 8 runtime is installed on the dev machine. Here the app runs on its actual target.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The runtime image ships neither curl nor wget, and a container healthcheck that cannot make an
# HTTP call can only prove the process exists - not that it still serves. curl is the smallest way
# to make the /health probe mean something.
USER root
RUN apt-get update \
 && apt-get install --yes --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Non-root. Orleans binds 11111 (silo) and 30000 (gateway), both unprivileged.
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PlayerService.Api.dll"]
