# Decisions

Living document; each phase adds its decisions. Requirement ids (ING-xx, CON-xx, HIS-xx) refer to the assignment brief.

## Architecture
- **Clean Architecture, explicit use cases.** Domain (no dependencies) ← Application (use cases + the ports they own) ← Infrastructure (EF Core, Npgsql) ← Api (composition root). An architecture test fails if Domain or Application reference EF, Npgsql, CsvHelper or ASP.NET Core. No MediatR: a use case is a plain class with `ExecuteAsync`, registered explicitly.
- **No generic repositories.** Each port exists for one concrete reason (a swappable implementation, an infrastructure-specific operation, or a read model). `AppDbContext` is `internal` to Infrastructure.
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
