# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Restore first, using only project files, so the layer is cached across code changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/MeridianVoteTracker.Domain/*.csproj src/MeridianVoteTracker.Domain/
COPY src/MeridianVoteTracker.Application/*.csproj src/MeridianVoteTracker.Application/
COPY src/MeridianVoteTracker.Infrastructure/*.csproj src/MeridianVoteTracker.Infrastructure/
COPY src/MeridianVoteTracker.Api/*.csproj src/MeridianVoteTracker.Api/
RUN dotnet restore src/MeridianVoteTracker.Api/MeridianVoteTracker.Api.csproj

COPY src/ src/
RUN dotnet publish src/MeridianVoteTracker.Api/MeridianVoteTracker.Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The import storage volume is initialized from this directory, so the non-root user can write to it.
RUN mkdir -p /app/storage && chown app:app /app/storage
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "MeridianVoteTracker.Api.dll"]
