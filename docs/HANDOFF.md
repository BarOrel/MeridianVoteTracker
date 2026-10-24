# Handoff: full working context (for continuing in a new chat / another machine)

Paste this file (or point the new assistant at it) at the start of a new session.

## 1. The assignment
- Take-home for a **Senior .NET Full-Stack Lead** role: *Meridian Vote Tracker*. The brief is in `AssignmentRequirements/README-Home-Assignment-dot-net.pdf` (Hebrew); sample screens are in `AssignmentRequirements/Meridian_Vote_Tracker_נספח_ב_*.pdf`.
- **Deadline: 2026-10-25 10:00.** Deliverables:
  - Git repo with meaningful commits.
  - `README.md` (how to run, how to test).
  - `DECISIONS.md` (max 2 pages): decisions/tradeoffs, scoring rules, DB and history design, Azure usage, prod changes, how to split the next phase among 4 devs, first standards.
  - `AI_USAGE.md` (which AI tools, where output was rejected or corrected).
- **Data** (`AssignmentRequirements/`): `member_states.csv`, `assembly_export_2024_q1.csv`, `assembly_export_2024_q1_rev2.csv`, `assembly_historic.csv` (962k rows).
- The scanned PDFs are an optional bonus. **Decided: NOT doing the PDF/Azure AI bonus.**

## 2. Working agreement with the user (keep following it)
- The user is under time pressure (family emergency). The priority is a **stable, demonstrable submission**: no refactors, no new abstractions unless strictly needed, keep the PostgreSQL integrity guarantees.
- Replies: **Hebrew, extremely concise**, covering: what was built · main flow (≤3 sentences) · one key decision · test status. Technical names stay in English.
- Don't ask approval for minor decisions; ask only if correctness or scope is materially affected.
- Run tests after each major feature; make a meaningful git commit per feature. Commit messages end with:
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- Clearly document unfinished requirements.

## 3. Remaining work, in priority order
1. **Conflicts (Phase 4)**:
   - List with filter by vote (CON-01).
   - Details with approved vs. proposed values and the source of each: file, uploader, time (CON-02).
   - Accept/Reject with comment (CON-04).
   - Atomic resolution; a concurrent second decision gets **HTTP 409** with the current state (CON-05).
   - Resolved conflicts stay viewable (CON-06).
   - Vote timeline (HIS-04).
2. **Minimal Angular UI**: Imports (upload, live progress, history, report) and Review (conflict list, side-by-side, accept/reject, 409 message).
   - The API client must be **generated from OpenAPI**, e.g. `ng-openapi-gen` from `/swagger/v1/swagger.json`.
   - A user switcher (dana / omar / lena) in the header sends `X-User-Id`.
3. Admin Position/Weight (POS-01..04) if time allows. The domain method `Vote.SetPosition` already exists.
4. Minimal immutable Snapshot only if safe.
5. Finalize README / DECISIONS (≤2 pages) / AI_USAGE.

## 4. Current state: committed and green
```
a09fc74 Phase 3: vote classification, approved-data persistence and import reports
9714b6c Phase 2: import ingestion foundation
770e3a4 Refactor persistence access to IApplicationDbContext
12b2e91 Phase 1: domain model, persistence, seed and role-based auth
c7ff6b8 Phase 0: solution skeleton, API host and Docker Compose
ca3d475 Add assignment brief and source data
```
- Tests: **103 unit + 46 integration passing** (`dotnet test`; integration needs Docker running).
- Working:
  - Upload (202).
  - Background worker: DB queue, `SKIP LOCKED`, lease, retry, crash recovery.
  - CSV parse and validate → COPY staging → per-vote classification → approved data + conflicts + audit.
  - Persisted report and rows endpoints.
  - Header auth with role policies.
  - Seed of 150 states and 3 users.
- **Verified on the real files** (exact numbers asserted in `tests/.../Imports/RealFilesScenarioTests.cs`):

  | File | New | Unchanged | Dup | Invalid | Contradiction | Conflict rows | Conflicts created |
  |---|---|---|---|---|---|---|---|
  | q1 | 5731 | 0 | 3 | 11 | 151 | 0 | 0 |
  | rev2 | 378 | 5576 | 0 | 0 | 0 | 6 | 8 (6 ballot + 2 metadata) |
  | rev2 again | 0 | 5954 | 0 | 0 | 0 | 6 | 0 |

  The 8 pending conflicts:
  - Ballots: MA/2024/003 BRA Y→A, MA/2024/003 IND Y→N, 010 JPN Y→X, 019 ZAF Y→N, 028 CAN Y→N, 028 MEX Y→A.
  - Metadata: 009 Title → "...: Revised"; 014 VoteDate 2024-02-05 → 2024-02-06.
- Historic file in Docker: staging ~17 s, classification ~80 s, ~200 MB memory; 962,109 new values, 6,480 votes, 30 duplicates, 32 invalid.

## 5. Architecture (do not change)
```
src/MeridianVoteTracker.Domain          entities, value objects, pure rules (no deps)
src/MeridianVoteTracker.Application     use cases by feature + ports; depends on EF Core abstractions only
src/MeridianVoteTracker.Infrastructure  EF Core 8 + Npgsql (snake_case), CsvHelper, workers, ports impl
src/MeridianVoteTracker.Api             composition root: controllers, X-User-Id auth, ProblemDetails, Swagger
tests/MeridianVoteTracker.UnitTests       (references Infrastructure only for the pure CSV reader)
tests/MeridianVoteTracker.IntegrationTests (Testcontainers PostgreSQL + WebApplicationFactory)
```
- **Use cases** are plain classes with `ExecuteAsync`, registered explicitly in `Application/DependencyInjection.cs`. No MediatR.
- **`IApplicationDbContext`** (Application):
  - `DbSet` only for `ImportJob`, `ImportRow`, `ImportVoteOutcome`.
  - Untracked `IQueryable` for `Votes`, `CountryVotes`, `Conflicts`, `VoteHistory`, `MemberStates`, `Users`.
  - No `SaveChanges`.
- **`IUnitOfWork.ExecuteInTransactionAsync`** is the only transaction boundary. No nesting. `SaveChanges` outside it throws.
- **Restricted-write guard:** `AppDbContext.SaveChanges` rejects writes to any type outside the allow-list unless they happen inside the internal `AllowRestrictedWrites()` scope.
  - The scope is used only by Infrastructure ports: the seeder and `PostgresApprovedVoteStore`.
  - **A restricted writer must flush (`SaveChangesAsync`) before disposing the scope**; the UoW still commits.
- **Specialized ports:**
  - `IImportFileStorage` (local disk volume).
  - `IImportFileReader` (CSV); `ImportFileReaderResolver` is a concrete class.
  - `IImportStagingStore` (COPY, write-once classification update).
  - `IImportJobStore` (`FOR UPDATE SKIP LOCKED`).
  - `IApprovedVoteStore`: `LockVotesAsync` (per-vote `pg_advisory_xact_lock`, sorted) and `SaveImportResultsAsync` (insert-only).
  - **Phase 4 should add the conflict-resolution write here.**
- **Concurrency tokens:** `country_votes.revision`, `votes.metadata_revision`, `votes.position_revision`, `conflicts.status`, `import_jobs.attempts`.
  - EF emits `UPDATE … WHERE token = @original`; a `DbUpdateConcurrencyException` → map it to 409.
- **Domain methods ready for Phase 4:**
  - `Conflict.Accept/Reject/Supersede`; they throw `DomainException(DomainErrors.ConflictAlreadyResolved)`.
  - `CountryVote.ApplyAcceptedConflict(conflict, at)` and `Vote.ApplyAcceptedConflict(conflict, at)`.
    - They check `ExpectedRevision` + `ApprovedValueAtDetection`, otherwise throw `DomainErrors.ApprovedValueChanged`.
    - They return a `VoteHistoryEvent` with old/new value and old/new provenance (import job + row).
  - `VoteHistoryEvent.ConflictRejected/ConflictSuperseded`; `Vote.SetPosition(position, weight, expectedRevision, actor, at)` → `PositionChanged` event.
- **DB guards (triggers, SQLSTATE `MV001`, `Persistence/AuditGuardsSql.cs` + migrations):**
  - `vote_history_events` is append-only.
  - `votes` / `country_votes` are never deleted; a value change must bump its revision.
  - `conflicts` are immutable once resolved.
  - `import_jobs`, `import_rows` and `import_vote_outcomes` are frozen once the job is finalized.
  - Membership periods have an exclusion constraint.
- **Errors:**
  - `UseCaseException(kind: Validation/NotFound/UnsupportedMediaType/Conflict, code, message, field)` is mapped to ProblemDetails by `Api/ErrorHandling/UseCaseExceptionHandler.cs`: 400 / 404 / 415 / 409, with `code` and `correlationId` extensions.
  - **`DomainException` is not mapped yet**: map `ConflictAlreadyResolved` / `ApprovedValueChanged` / `PositionChanged` → 409.
- **Auth:**
  - The `X-User-Id` header resolves the user via `GetUserUseCase`.
  - Policies `AuthorizationPolicies.Analyst` (Analyst+Admin) and `.Admin`.
  - A global `AuthorizeFilter` requires a user on every controller action. Users: `dana`, `omar` (Analyst), `lena` (Admin).

## 6. Key business decisions (already in DECISIONS.md)
- **Normalization:**
  - Allowed: ISO dates only (`dd/mm/yyyy` → Invalid); trimming surrounding whitespace and uppercasing the country code are normalizations, recorded in `normalization_notes` and shown in the report.
  - Never normalized: ballot values (`Yes` → Invalid; rev2 proves that row was really `A`) and inner title whitespace.
- **Duplicates:** Exact (raw fields equal) vs. AfterNormalization (same facts). Both are classified Duplicate, with a `DuplicateKind`.
- **Contradictions:**
  - Rows of a vote that disagree on session/date/title → the whole vote is a Contradiction and nothing is stored.
  - A state with two ballots → both rows are Contradiction.
- **Conflicts:** a pending proposal is linked, not reopened; a proposal already rejected against the same revision is linked to that rejection and not reopened.
- **History:** event log (`vote_history_events`) plus provenance columns on approved rows (no event per initial value).
- **Scoring rules (for snapshots, not built yet):**
  - A and X are not counted.
  - Votes where the state was not a member: no row exists, so not counted.
  - A state with no counted vote → `n/a`, unranked.
  - Competition ranking (1, 2, 2, 4).
  - A vote without a position blocks snapshot creation (POS-04).

## 7. API so far
- `GET /api/users` (anonymous), `GET /api/users/me`
- `POST /api/imports` (multipart `file`, Analyst) → 202; `GET /api/imports/{id}`, `GET /api/imports/{id}/report`, `GET /api/imports/{id}/rows?classification=&normalizedOnly=&page=&pageSize=`
- `/health/live`, `/health/ready`, `/swagger`
- Missing: `GET /api/imports` (history list, needed by the UI), conflicts endpoints, votes endpoints.

## 8. How to run
```bash
cp .env.example .env
docker compose up --build            # API on http://localhost:8080 (swagger at /swagger)
dotnet test                          # needs Docker for integration tests
dotnet tool restore && dotnet ef migrations add <Name> --project src/MeridianVoteTracker.Infrastructure --output-dir Persistence/Migrations
```
- .NET SDK **8.0.4xx** is pinned by `global.json` (install the .NET 8 SDK on the new machine).
- Integration tests use 3 fixtures:
  - `ApiFactory`: worker off.
  - `ImportApiFactory`: worker on.
  - `RealFilesApiFactory`: own DB for the real-file scenario.

## 9. Gotchas learned
- **Windows Python:** use `PYTHONUTF8=1` (or `encoding='utf-8'`) when editing files; cp1252 silently broke an edit once. Prefer the Edit tool for multi-line C# edits; bash heredocs with `'\n'` inside Python strings caused mismatches.
- The working copy is CRLF (git `text=auto`). CSV fixtures are `-text` (BOM and CRLF preserved).
- EF does not order statements reliably within one `SaveChanges`: flush row writes before finalizing a job (the trigger freezes the rows of finalized jobs).
- `AssemblyVoteId` (record struct) is not `IComparable`: order in memory by `.Value` with `StringComparer.Ordinal`.
- LINQ over converted value objects works for `==`, `Contains` (use a `List<AssemblyVoteId?>` against the nullable column), `OrderBy` (in SQL).
- Workstation GC is set in the Api csproj (Server GC reserved ~590 MB).
- Route ids: `Vote.Id` (int) is used in routes, never `MA/2024/003` (it contains slashes).

## 10. Plan for Phase 4 (start here)
- **Application `Conflicts/`:**
  - `ListConflictsUseCase` (status filter, default Pending; vote filter).
  - `GetConflictUseCase`: approved vs. proposed values; source file name, uploader and upload time of both the approved value (provenance) and the proposal; resolution info.
  - `ResolveConflictUseCase(id, Accept|Reject, comment)`:
    - one UoW;
    - `IApprovedVoteStore.LockVotesAsync([vote])`;
    - load the tracked conflict + approved row through a new `IApprovedVoteStore` method;
    - domain Accept/Reject + `ApplyAcceptedConflict`;
    - supersede other pending conflicts on the same fact;
    - write the events;
    - `DbUpdateConcurrencyException`/`DomainException` → `UseCaseException(Conflict)` → **409** with the current state.
- **Votes:** `GetVoteTimelineUseCase` reads `VoteHistory` (untracked) by vote id. Minimal vote list/detail if needed by the UI.
- **Tests:** concurrent accept + reject of the same conflict → one 200 and one 409; accepting two conflicts on the same fact → second 409; the timeline shows old/new values with the source import; a resolved conflict is still viewable.
