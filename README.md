# Meridian Vote Tracker

Imports Meridian Assembly vote exports, keeps approved data trustworthy (no silent overwrites, human decisions on every conflict, full history) and calculates immutable, reproducible Alignment Index snapshots.

**Stack:** ASP.NET Core 8, EF Core 8 + Npgsql, PostgreSQL 16, Angular 20, Docker Compose, xUnit + Testcontainers.

- Design decisions: [DECISIONS.md](DECISIONS.md) (detailed notes: [docs/DESIGN-NOTES.md](docs/DESIGN-NOTES.md))
- AI usage: [AI_USAGE.md](AI_USAGE.md)

## Run everything
```bash
docker compose up --build       # works on a fresh clone; local defaults are built in
# optional: cp .env.example .env  to override the DB name/user/password or the ports
```

| | URL |
|---|---|
| Web app | http://localhost:4200 |
| API + Swagger | http://localhost:8080/swagger |
| Health | http://localhost:8080/health/ready |

On start-up the API applies migrations and seeds the 150 member states and three users. The seed is idempotent.

**Users** (switch in the header; the UI sends `X-User-Id`; roles are enforced by the API):

| User | Role | Can |
|---|---|---|
| `dana`, `omar` | Analyst | upload, review and decide conflicts, create snapshots |
| `lena` | Admin | everything above, plus positions/weights and publishing snapshots |

## Demo script (the brief's story, with the real files)
1. **Imports** (as Dana): upload `AssignmentRequirements/assembly_export_2024_q1.csv`.
   - Live progress appears, then the report: 5,731 new · 3 duplicates · 11 invalid · 151 contradictions.
   - All of MA/2024/025 is blocked (three different titles); FRA in MA/2024/012 is contradictory.
   - Rows show their row number, reason and original text; `deu → DEU` is shown as a normalization.
2. Upload `assembly_export_2024_q1_rev2.csv` (switch to Omar if you like).
   - 378 new (completions of partial votes, previously rejected rows, the new vote MA/2024/041) · 5,576 unchanged.
   - **8 conflicts:** 6 ballots + 2 vote details. Approved data is unchanged.
3. Upload rev2 **again**: 0 new values, 0 new conflicts; the 6 rows are linked to the conflicts already pending.
4. **Review:** open MA/2024/003 · BRA (approved YES vs incoming ABSTAIN, each with file, uploader and time). Accept it with a comment.
   - In a second browser as Omar, reject the same conflict: you get "dana already resolved this conflict…" and the current state. **HTTP 409**, nothing overwritten.
5. **Votes** → MA/2024/003: BRA is now ABSTAIN, marked "changed by a conflict decision".
   - The timeline shows Y → A, from which file each value came, who decided and the comment.
   - As Lena, set the position and weight. Weight 7 shows a field error; a stale edit gets 409.
6. **Snapshots:** try one for 2024-01-01..2024-03-31 first.
   - It is blocked until every selected vote has a position; the error lists them.
   - For a quick demo, set positions (as Lena) on two or three votes, e.g. MA/2024/001 and MA/2024/003, and create the snapshot from that vote list.
   - When it completes, open it: ranking, a per-country breakdown and the scoring rules.
   - As Lena, mark it published for `2024-Q1`.
   - Accept another conflict and reopen the snapshot: its numbers do not change. A new snapshot uses the new value.

Also try `assembly_historic.csv` (962k rows, rows of a vote not contiguous): ~17 s staging and ~80 s classification in Docker on a laptop.

## Local development
```bash
docker compose up -d db
dotnet user-secrets --project src/MeridianVoteTracker.Api set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=meridian-local-dev"   # or your .env value
dotnet run --project src/MeridianVoteTracker.Api          # http://localhost:5244
cd web/meridian-web && npm ci && npm start                 # http://localhost:4200 (proxies /api to :8080)
```
- **The API client is generated, never hand-written:** with the API on :8080, run `npm run api:fetch && npm run api:generate` (ng-openapi-gen → `src/app/api`).
- **Migrations:** `dotnet tool restore && dotnet ef migrations add <Name> --project src/MeridianVoteTracker.Infrastructure --output-dir Persistence/Migrations`.
- **Prerequisites:** .NET SDK 8.0.4xx (pinned in `global.json`), Node 20+, and Docker (also used by the integration tests).

## Tests
```bash
dotnet test
```

| Project | What |
|---|---|
| `MeridianVoteTracker.UnitTests` (103) | domain rules, classifier (the brief's §7.2 example and edge cases), CSV reader, validator, import lifecycle, architecture (dependency rule, persistence surface) |
| `MeridianVoteTracker.IntegrationTests` (52) | real API against **real PostgreSQL** (Testcontainers): see below |

The integration tests cover:
- The real files q1 → rev2 → rev2, with exact counts.
- A historic-file sample.
- Crash recovery during staging and during classification.
- Lease takeover.
- Concurrent imports of the same data.
- Concurrent decisions: one 200 and one 409.
- Two proposals for the same fact: one wins, the other is superseded.
- Immutability triggers.
- Position rules.
- Snapshots: 66.7 example, unchanged after a correction, one publication per quarter.

## Structure
```
src/MeridianVoteTracker.Domain          entities, value objects, pure rules (VoteImportClassifier, Conflict, Vote…)
src/MeridianVoteTracker.Application     use cases by feature (Imports, Conflicts, Votes, Snapshots, Users) + ports
src/MeridianVoteTracker.Infrastructure  EF Core, migrations + SQL guards, CSV reader, COPY stores, workers
src/MeridianVoteTracker.Api             controllers, X-User-Id auth + policies, ProblemDetails, OpenAPI
web/meridian-web                        Angular app (generated client in src/app/api)
tests/                                  unit + integration tests
AssignmentRequirements/                 source CSVs used by the tests and the demo (kept byte-for-byte)
```

## Requirements coverage
| Area | Status |
|---|---|
| ING-01..08, 10..15 | Done. ING-09: absent votes are never touched, but the report does not list "votes not in this file". |
| CON-01..06 | Done (409 with the current state; the UI shows who decided). |
| HIS-01..04 | Done (file, rows, report, event log with old/new value and source, timeline). |
| POS-01..04 | Done (admin only in the API, field errors, history, missing position blocks snapshots). |
| SNP-01..05 | Done. Status is shown while calculating, but without a percentage. Measured manually on the full history (6,480 votes, ~962k ballots): ~31 s, results match an independent SQL recomputation; there is no automated test at that size. |
| §7.7 frontend | Done: four screens, generated client, field errors, live status, 409 message. Minimal styling. |
| §9 technical | OpenAPI ✓ · uniform ProblemDetails ✓ · migrations + constraints ✓ · real-PostgreSQL tests ✓ · no secrets in code ✓ · Correlation id: per request and in worker logs (stored on import jobs); not sent by the UI and no AI calls |
| §8 PDF bonus / Azure resources | **Not done** (deliberate; see DECISIONS §4) |

## Known limitations
- Authentication is simulated: anyone can send any known `X-User-Id`, as the brief allows.
- Progress is polled every 2 s, not pushed; there is one worker of each kind per API process (multiple instances are safe).
- The import report returns all per-vote outcomes in one response; the 6,480 votes of the historic file are not paged.
- An accepted vote-date correction keeps the existing approved ballots, even those of a state that was not a member on the new date. Snapshots do not count such ballots; the vote screen still lists them.
- CSV: an unterminated quote merges the following lines into one invalid record (RFC 4180 behaviour), and `session` values with leading zeros (`079`) are accepted without a normalization note.
- During staging the total row count is not yet known, so progress shows rows read.
- Snapshot selection filters (dates, session, vote list) combine with AND. Inputs are captured when the snapshot is calculated, a moment after its creation.
- The UI does not send a correlation id; the server creates one per request and stores it on import jobs.
