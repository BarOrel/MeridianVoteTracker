# Design notes (detailed)

The full reasoning behind [DECISIONS.md](../DECISIONS.md), written phase by phase. Requirement ids (ING-xx, CON-xx, HIS-xx) refer to the assignment brief.

## Architecture
- **Clean Architecture, explicit use cases.** Domain (no dependencies) ← Application (use cases + the ports they own) ← Infrastructure (EF Core, Npgsql) ← Api (composition root). An architecture test fails if Domain or Application reference EF, Npgsql, CsvHelper or ASP.NET Core. No MediatR: a use case is a plain class with `ExecuteAsync`, registered explicitly.
- **Persistence access: `IApplicationDbContext`, no repositories.**
  - Use cases query and write through `IApplicationDbContext` (Application layer), implemented by the internal `AppDbContext`; both it and `IUnitOfWork` resolve to the same scoped instance.
  - *Accepted tradeoff:* Application depends on EF Core abstractions (`DbSet`, async LINQ). Rationale: per-entity repositories would mostly be thin wrappers that hide nothing. Guard: an architecture test allows `Microsoft.EntityFrameworkCore` but forbids Relational, Npgsql and Infrastructure in Application; Domain stays dependency-free.
  - `DbSet<T>` is exposed only for workflow entities use cases write directly (`ImportJob`, `ImportRow`).
  - Approved data, conflicts, the audit trail and reference data are exposed as untracked `IQueryable<T>`, so they are read-only by construction.
  - There is no `SaveChanges` on the interface; `IUnitOfWork` commits.
- **Specialized ports only where SQL-specific or invariant-critical work happens.** They are added in the phase that needs them; no speculative interfaces:
  - `IImportStagingStore`: binary COPY.
  - `IImportJobStore`: claim/lease with `SKIP LOCKED`.
  - `IApprovedVoteStore`: atomic approved-data writes with conditional updates and audit events.
- **Restricted-write safeguard (two layers).**
  1. Compile time: no `DbSet` for restricted types in `IApplicationDbContext`.
  2. Run time: `AppDbContext.SaveChanges` rejects any added, modified or deleted entity outside an allow-list (`ImportJob`, `ImportRow`; owned types judged by their owner) unless the write happens inside `AllowRestrictedWrites()`.

  `AllowRestrictedWrites()` is `internal`, so only Infrastructure ports (the seeder, later `IApprovedVoteStore`) can open it. A restricted writer must flush inside its scope; anything still pending when the scope closes is rejected. The allow-list means a new entity type is restricted by default. The database triggers below remain the last line of defense.
- **.NET 8 (`net8.0`)**, pinned by `global.json`.

## Data integrity
- **Two ways to create or change approved data, nothing else.**
  - `CountryVote.CreateFromImport` / `Vote.CreateFromImport` create a fact that did not exist ("New"; provenance `InitialImport`).
  - `ApplyAcceptedConflict` changes a fact after a human decision (provenance `ConflictResolution`).
  - There is no method an import could use to modify an existing value (ING-08).
- **Lost-update protection.** Every approved fact has a revision (`country_votes.revision`; `votes.metadata_revision` and `position_revision`) mapped as an EF concurrency token. Accepting a conflict runs in one transaction with two conditional updates:
  1. `UPDATE conflicts … WHERE id=@id AND status='Pending'`
  2. `UPDATE country_votes … WHERE … AND revision=@expected`

  If either update matches no row, the whole decision rolls back, so two different conflicts on the same fact cannot both win. The domain additionally checks `ExpectedRevision` and `ApprovedValueAtDetection` captured at detection time, so a decision is applied only against the value the analyst actually saw. On `votes`, both counters are tokens, which is conservative: a concurrent position edit and metadata correction cause a retry, never a lost update.
- **Pending conflicts are unique** per fact and proposed value (partial unique indexes), which keeps re-imports idempotent (ING-12) even under concurrency.
- **Transactions.** A use case owns the boundary through `IUnitOfWork.ExecuteInTransactionAsync`:
  - Nesting throws.
  - `SaveChanges` outside a unit of work throws, so there are no implicit EF transactions.
  - Code may flush inside a boundary (e.g. to get identity values); the commit happens once, at the end.
  - A failure rolls back and clears the change tracker.
  - Isolation is Read Committed; correctness comes from conditional updates and constraints, not from Serializable.

## Audit and history (HIS-01..04)
- **Event log rather than revision tables.** `vote_history_events` is the vote timeline: old/new value, old/new provenance (import job + row number), actor, time, conflict and comment.
- **Initial values carry their provenance on the row itself** (`approval_source_import_job_id`, `approval_source_row_number`, `approved_by`, `approved_at`), plus one `CountryVotesAdded` event per vote and import. This avoids ~1M extra rows for the historic import, while every *change* writes a full event that preserves the previous provenance. The chain "current value ← every previous value ← source file and row" is therefore always reconstructable.
- **Tamper resistance in the database** (triggers raising SQLSTATE `MV001`):

  | Table | Guard |
  |---|---|
  | `vote_history_events` | append-only (no UPDATE, DELETE, TRUNCATE) |
  | `votes`, `country_votes` | never deleted (ING-09); identity immutable; any value change must increment its revision |
  | `conflicts` | never deleted; immutable once resolved (CON-06) |
  | `import_jobs` | never deleted; immutable once finalized |
  | `membership_periods` | no overlap (exclusion constraint) |

  Production hardening, not done: a DB role with only INSERT/SELECT on audit tables and no TRUNCATE/DDL.

## Import rows: lifecycle, raw vs normalized
- **Lifecycle.** A row is *Staged* (valid and awaiting classification, or already *Invalid*), then *Classified* exactly once.
  - While the job is processing, rows are writable. A crash during staging deletes and restages the job's rows; classification resumes per vote batch.
  - Once the job is Completed, CompletedWithIssues or Failed, a trigger rejects INSERT/UPDATE/DELETE on its rows.
  - **Persistence rule:** finalizing the job must be the last write, after row writes are flushed. EF does not guarantee statement order within one `SaveChanges`; a test caught exactly this.
- **Raw input is kept independently of normalized values.**
  - Stored per row: `original_text`, eight `raw_*` columns (as read, no trimming), typed normalized columns, and `normalization_notes` (text[]) listing every change made, e.g. `country_code: 'deu' → 'DEU'`. The report shows the notes (ING-13).
  - Allowed normalizations change representation, not meaning: trimming surrounding whitespace and uppercasing the country code.
  - Never normalized:
    - ballot values (`Yes`, `YES` and `0` are Invalid; rev2 shows a `Yes` row was really `A`)
    - date formats (`21/02/2024` is Invalid)
    - whitespace inside titles (a double space is a real difference)
    - country names, which are not identity (ING-11)
- **Exact duplicates vs. equivalent after normalization.** Both are classified **Duplicate**, keeping the single-classification rule (ING-06), but they are distinguished explicitly:
  - `DuplicateKind.Exact`: all raw fields are byte-identical to an earlier row ("noise", as in the brief).
  - `DuplicateKind.AfterNormalization`: raw text differs but the normalized record is identical. The report lists both rows, the kind and the normalization note, so nothing is hidden.

  The first occurrence is classified on its merits; copies reference it (`duplicate_of_row_number`). Rows that differ in *meaning* after normalization are never duplicates; they are Contradictions.

## Import pipeline (ING-01..05, ING-07, ING-13)
- **Stages are separate classes with one job each:**
  - `IImportFileStorage`: durable file.
  - `IImportFileReader`: format syntax to `RawImportRecord`.
  - `ImportRecordValidator`: row rules and recorded normalization.
  - `IImportStagingStore`: bulk write.
  - Classification against approved data (Phase 3) is not mixed into any of them.
- **The `RawImportRecord` contract:** row number, original text, eight raw text fields or a structural error. It is the only thing that crosses from a reader to the rest of the pipeline, so a new format (Excel, JSON) is one new `IImportFileReader` plus a DI registration. Format is detected at upload (name + first bytes) and stored on the job, so retries use the same reader.
- **Bad records never stop an import.** Readers yield malformed records (wrong column count, broken quoting, NUL) with a reason; values that would not fit their column (title, record length) are Invalid rows, not failed batches. Only a file that is unusable as a whole (missing header columns, missing stored file) fails the job, without retry.
- **Bounded memory.**
  - The file is streamed.
  - Rows go to PostgreSQL in batches of 5,000 via binary COPY on the unit of work's connection, so each batch commits with the job's progress and lease heartbeat.
  - Rows are never tracked by EF.
  - Grouping by vote (rows are not contiguous, ING-03) happens later in SQL over the staged rows, not in memory.
  - Measured on the 962,171-row historic file in Docker: ~17 s staging, API memory flat at ~110 MB (Workstation GC; Server GC reserved ~590 MB, hence the setting).
- **Validation rules (row level).** Required fields, vote id format, positive session, ISO date only, known country, state was a member on the vote date, ballot exactly Y/N/A/X, `published_at` optional but ISO 8601 with offset if present, length limits. All problems of a row are reported; the first is its reason code.
- **Background processing: a database-backed queue instead of a broker.** `import_jobs` is the queue.
  - Claiming uses `FOR UPDATE SKIP LOCKED` in a short transaction that sets a lease (worker id + expiry).
  - Every batch re-verifies ownership, and `attempts` is a concurrency token, so a worker that lost its lease cannot write.
  - A crashed worker's job is claimed again when the lease expires; staging restarts from the file, deleting that job's staged rows.
  - Transient failures are retried after 15 s × attempt, up to 3 attempts, with the error visible in the status; file-level problems fail immediately.
  - *Lost vs. a broker:* no push delivery (2 s polling) and one process; acceptable for this load and keeps one durable store.
- **Classification (Phase 3).**
  - Staged rows are grouped by vote in SQL (`DISTINCT assembly_vote_id … WHERE classification IS NULL`), 200 votes per transaction. File order is irrelevant (ING-03), and the same query is the resume point after a crash: a vote is either fully classified (committed) or untouched.
  - One batch transaction contains:
    - per-vote advisory locks;
    - reads of the current approved state;
    - the pure `VoteImportClassifier`;
    - inserts of new votes, country votes, conflicts and history events through `IApprovedVoteStore`;
    - the row classifications (COPY to a temp table plus `UPDATE … WHERE classification IS NULL`; a row-count mismatch rolls the batch back);
    - the per-vote outcomes and the job's progress.
  - The job is completed in a separate, final transaction, only after verifying that no unclassified row remains.
- **Rule order per vote** (rows in file order; first occurrence wins):
  1. Exact duplicate (all raw fields equal), then duplicate after normalization (same vote details, state and ballot; a different country *name* alone is a note, not a difference).
  2. Rows disagreeing on session/date/title: the whole vote is a Contradiction and nothing of it is stored. Choosing the majority would be a silent fix.
  3. A state with different ballots in one file: Contradiction, neither stored.
  4. Against approved data: New, Unchanged or Conflict; missing states of an existing vote are New (ING-10).
  5. Vote details different from the approved vote open metadata conflicts.
- **Conflict dedupe (ING-12).** A pending conflict with the same target and proposed value is linked, not reopened. A proposal already *rejected* against the same approved value and revision is linked to that rejection and not reopened; if the approved value changed since, it is a new conflict. The partial unique indexes back this up under concurrency.
- **Imports never update approved data.** `IApprovedVoteStore.SaveImportResultsAsync` only inserts. Concurrent imports of the same vote are serialized by `pg_advisory_xact_lock` per vote (taken in sorted order in one statement: no deadlocks), so the second import sees the first one's values. Tested with two concurrent imports of the same file and of the same correction.
- **Report** (persisted, ING-14/15). Derived from `import_rows` and `import_vote_outcomes`, both frozen by triggers once the job is finalized.
  - Totals per classification; exact vs. after-normalization duplicates; normalized rows.
  - Conflicts created by this import (ballot / metadata), and rows linked to already pending or previously rejected conflicts.
  - Per vote: outcome, counts, notes such as name variants or metadata conflicts.
  - Paged problem rows with row number, reason and original text.
- **Measured (Docker).** The historic file classified in ~80 s (962,109 new values, 6,480 votes) at ~200–240 MB; re-importing it took ~52 s and changed nothing.

## Identity and authorization
- **Simplified identity, as the brief allows.** The `X-User-Id` header carries a pre-defined user; the server looks up the user and role.
- **Role policies are enforced by the API.**
  - A global `AuthorizeFilter` requires a user on every controller action unless `[AllowAnonymous]`.
  - `Analyst` allows Analyst or Admin; `Admin` allows Admin only.
  - Errors are ProblemDetails: 401 for a missing or unknown user, 403 for a wrong role.
  - Limitation: anyone can send any known user id; not production authentication.

## Reference data
- `member_states.csv` is embedded in Infrastructure and seeded at start-up in one transaction. The seed is idempotent (adds only missing states, periods and users) and never updates existing rows: for a different name it keeps the stored one and logs a warning. Malformed lines stop start-up with the line number.
- **Date format.** The brief's ING-04 says `dd-mm-yyyy`; Appendix A and all data files use `yyyy-mm-dd`. ISO `yyyy-mm-dd` is the canonical format; anything else is Invalid.
