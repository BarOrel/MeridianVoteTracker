# Decisions

Short version (≤ 2 pages). Full reasoning per topic: [docs/DESIGN-NOTES.md](docs/DESIGN-NOTES.md).

## 1. Key decisions and trade-offs
- **Order of work.** Data integrity first: import → classification → conflicts → UI → snapshots. The core was proven on the real files before building screens (e.g. q1 → rev2 → rev2 gives exactly 8 conflicts, then 0 new).
- **Clean Architecture, explicit use cases, no repositories.**
  - Use cases talk to `IApplicationDbContext`. It exposes `DbSet` only for workflow data; approved data, conflicts and history are untracked read-only `IQueryable`.
  - Accepted trade-off: Application depends on EF Core abstractions, not on a provider.
  - Writes to approved data go only through `IApprovedVoteStore`, enforced twice:
    - a runtime guard rejects other writes at `SaveChanges`;
    - database triggers.
- **Background work uses a database queue, not a broker.**
  - `import_jobs` and `snapshots` are the queues; `FOR UPDATE SKIP LOCKED` plus a lease (`attempts` is a concurrency token).
  - Crash recovery: staging restarts from the stored file; classification resumes per vote batch; nothing committed is redone.
  - What a broker would add (push delivery, scale-out) isn't needed at this load; what this design keeps is a single durable store.
- **Bulk path.** Rows are streamed and written with binary COPY (5k per transaction), grouped by vote in SQL (200 votes per transaction).
  - Historic file in Docker: 962k rows staged in ~17 s, classified in ~80 s, at ~200 MB.
  - Workstation GC: Server GC reserved ~590 MB.
- **Validation and normalization.**
  - Representation-only normalizations, always recorded and shown: trimming, uppercasing the country code.
  - Never "fixed": ballots (`Yes` is Invalid; rev2 shows that row was really `A`), date formats (`21/02/2024`), inner title whitespace, country names (identity is the code).
  - The brief's `dd-mm-yyyy` contradicts Appendix A and the data; ISO `yyyy-mm-dd` is canonical.
- **Classification per vote, in file order.**
  - Duplicate: *Exact* (identical raw fields) or *AfterNormalization* (same facts). Both are counted as Duplicate, with a kind.
  - Rows of a vote that disagree on session/date/title: the whole vote is a Contradiction, because choosing the majority would be a silent fix.
  - A state with two ballots: Contradiction, neither stored.
  - Otherwise New / Unchanged / Conflict. Missing states are New (ING-10). Differing vote details open metadata conflicts.
  - A pending proposal is linked, not reopened; a proposal already *rejected* against the same approved revision is not reopened (ING-12).
- **Concurrency without lost updates.**
  - A per-vote `pg_advisory_xact_lock` serializes imports and decisions on the same vote.
  - A decision is one transaction: conditional update of the conflict (`status = 'Pending'`) and of the approved value (`revision = expected`, value equal to the one the analyst saw); other pending proposals for the same fact are Superseded.
  - The loser gets **409** with the current state (who decided, when). The UI shows it, not a generic error.
- **Auth.** Header `X-User-Id` with pre-defined users; the role is resolved server-side; `Analyst` / `Admin` policies enforced by the API (positions and publishing are Admin only).

## 2. Scoring rules (decided; shown on every snapshot)
- Score = 100 × Σ weights where the ballot matched the position ÷ Σ weights of counted votes.
- Only **YES/NO are counted**; ABSTAIN and ABSENT are excluded from both sums.
- Votes held while a state was not a member have no ballot (rejected at import), so they are not counted.
- A state with no counted vote gets **n/a** and is unranked.
- Ranking: score descending, ties share a rank (1, 2, 2, 4); one decimal.
- A selected vote without a position **blocks creation** (POS-04); the 400 lists the votes.

## 3. Database and history
- **Approved state** lives in `votes` and `country_votes`. Each value carries its provenance on the row: import job, row number, approved by/at, origin (InitialImport / ConflictResolution) and a revision.
- **History** is an append-only event log, `vote_history_events`. It is used instead of revision tables:
  - every change stores old/new value, old/new source (file + row), actor, time, conflict and comment;
  - initial values cost no extra rows (their provenance is on the row);
  - the vote timeline (HIS-04) is a query over it.
- **Import audit (HIS-01):** the stored file (SHA-256), `import_rows` (original text, raw fields, normalized fields, normalization notes, classification, reason) and `import_vote_outcomes`.
- **Snapshots** copy their inputs (positions, weights, ballots) into `snapshot_votes` and `snapshot_entries` and compute `snapshot_results` in one SQL statement. That gives one consistent view, and the snapshot references no mutable data.
- **Guards in PostgreSQL** (triggers, constraints):
  - history is append-only;
  - approved rows are never deleted, and a change must bump its revision;
  - resolved conflicts, finalized imports and completed snapshots are immutable;
  - one pending conflict per fact and value (partial unique index);
  - one published snapshot per quarter (PK);
  - membership periods cannot overlap (exclusion constraint).

## 4. Azure
None of the Azure resources were used, deliberately: the core runs locally with `docker compose up`, and the optional PDF/AI bonus was not attempted. The design maps directly:
- PostgreSQL → **Azure Database for PostgreSQL** (connection string via config).
- `IImportFileStorage` → **Blob Storage** (one new implementation).
- Secrets: `.env` / user-secrets → **Key Vault** references.
- Logs (correlation id already in every log scope, stored on import jobs) → **Application Insights**.
- Cosmos DB is not a fit: the data is relational and constraint-heavy.

## 5. Before production
- Real identity: Entra ID / OIDC, roles from claims instead of `X-User-Id`.
- A DB role with INSERT/SELECT only on audit tables, no TRUNCATE/DDL.
- Run migrations from the pipeline, not at start-up.
- Blob storage and Key Vault; App Insights with correlation from the UI through the API and the workers.
- Multiple workers (already safe with `SKIP LOCKED`); push progress (SignalR) instead of polling.
- Paging for the per-vote report on huge imports; retention and backup policy; an automated load test for snapshots on the full history (measured manually: ~31 s).

## 6. Next phase with four developers
1. **Ingestion:** more formats as new `IImportFileReader`s, Blob storage, scale-out workers.
2. **Review and history:** bulk decisions, richer timeline and audit exports.
3. **Index and snapshots:** bulk position editing, snapshot comparison, performance on the full history.
4. **Frontend and platform:** design system, e2e tests (Playwright), CI/CD, observability.

First standards:
- The dependency rule, enforced by the architecture tests that exist.
- Every rule change starts with a failing test; integration tests always run on real PostgreSQL.
- The API contract is OpenAPI-first and the client is generated, never hand-written.
- ProblemDetails for every error.
- Migrations reviewed like code, including triggers.
- Small PRs with required review and green CI.
