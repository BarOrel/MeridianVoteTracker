# Meridian Vote Tracker

Take-home assignment: ingestion, review and ranking of Meridian Assembly votes.
ASP.NET Core 8 · PostgreSQL 16 · EF Core · Angular · Docker Compose.

> Status: **Phase 0 — skeleton.** Solution layout, dependency rule, API host, Docker Compose and tests are in place. No domain features yet.

## Prerequisites
- .NET SDK 8.0.4xx (pinned by `global.json`)
- Docker Desktop (Compose v2) — also required by the integration tests (Testcontainers)

## Run with Docker Compose
```bash
cp .env.example .env        # adjust the password
docker compose up --build
```
- API: http://localhost:8080 · Swagger UI: http://localhost:8080/swagger
- Health: `/health/live` (process up), `/health/ready` (PostgreSQL reachable)
- On start-up the API applies EF Core migrations and seeds member states and users (idempotent).

## Users (simplified authentication)
Send the header `X-User-Id` with one of the pre-defined users (Swagger UI: "Authorize" button):

| Id | Role | Can |
|---|---|---|
| `dana`, `omar` | Analyst | upload, review, resolve conflicts, create snapshots |
| `lena` | Admin | everything an analyst can, plus positions/weights and publishing |

Roles are resolved server-side and enforced by API policies.

## Run the API locally (DB in Docker)
```bash
docker compose up -d db
dotnet user-secrets --project src/MeridianVoteTracker.Api set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=<from .env>"
dotnet run --project src/MeridianVoteTracker.Api
```
No connection strings or secrets are committed: Compose reads `.env` (git-ignored), local runs use user-secrets.

## Database migrations
```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/MeridianVoteTracker.Infrastructure --output-dir Persistence/Migrations
```

## Tests
```bash
dotnet test
```
- `MeridianVoteTracker.UnitTests` — fast, no Docker (domain rules, architecture/dependency-rule guard).
- `MeridianVoteTracker.IntegrationTests` — real API (`WebApplicationFactory`) against a real PostgreSQL container.

## Solution layout
```
src/
  MeridianVoteTracker.Domain          entities, value objects, pure domain rules (no dependencies)
  MeridianVoteTracker.Application     use cases by feature + ports (interfaces) they own
  MeridianVoteTracker.Infrastructure  EF Core/Npgsql, file readers, storage, background workers
  MeridianVoteTracker.Api             composition root: HTTP, auth, ProblemDetails, OpenAPI
tests/
  MeridianVoteTracker.UnitTests
  MeridianVoteTracker.IntegrationTests
AssignmentRequirements/               assignment brief and source data (kept byte-for-byte, see .gitattributes)
```
