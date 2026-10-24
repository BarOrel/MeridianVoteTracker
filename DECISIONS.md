# Decisions

Two-page summary; full reasoning in [docs/DESIGN-NOTES.md](docs/DESIGN-NOTES.md).

## 1. Key decisions and trade-offs
- **Order of work.** Data integrity first: import → classification → conflicts → UI → snapshots. The core was proven on the real files before any screen: q1 → rev2 → rev2 gives exactly 8 conflicts, then 0 new.
- **Clean Architecture, explicit use cases, no repositories.** Use cases use `IApplicationDbContext`: `DbSet` only for workflow data; approved data, conflicts and history are read-only `IQueryable`. Trade-off: Application depends on EF Core abstractions, not on a provider. Approved data is written only by `IApprovedVoteStore`, guarded by a `SaveChanges` check and by database triggers.
- **Database queue, not a broker.** `import_jobs` and `snapshots` are claimed with `FOR UPDATE SKIP LOCKED` plus a lease. After a crash, staging restarts from the stored file and classification resumes per vote batch. Push delivery and scale-out are lost; neither is needed at this load.
- **Bulk path.** Streamed parsing, binary COPY (5k rows per transaction), grouping by vote in SQL (200 votes per transaction). The 962k-row historic file stages in ~17 s and classifies in ~80 s at ~200 MB.
- **Validation and normalization.** Only representation is normalized (trimming, uppercasing the country code), always recorded and shown. Ballots, dates and inner whitespace are never "fixed": `Yes` is Invalid, and rev2 proves it was `A`. ISO `yyyy-mm-dd` is canonical; the brief's `dd-mm-yyyy` contradicts Appendix A and the data.
- **Classification per vote, in file order.**
  - Duplicate: *Exact* (identical raw fields) or *AfterNormalization* (same facts); the second is a documented extension of the brief.
  - A vote whose rows disagree on session, date or title is entirely a Contradiction, because picking the majority would be a silent fix.
  - A state with two ballots: Contradiction, neither stored. Otherwise New / Unchanged / Conflict; missing states are New.
  - Differing vote details open metadata conflicts. Pending or already-rejected proposals are linked, not reopened.
- **No lost updates.** A per-vote `pg_advisory_xact_lock` serializes imports and decisions. A decision is one transaction with conditional updates on the conflict (`status = 'Pending'`) and on the approved value (expected revision and value); other proposals for the same fact are Superseded. The loser gets **409** with the current state.
- **Auth.** The `X-User-Id` header carries a pre-defined user; the role is resolved on the server; `Analyst`/`Admin` policies are enforced by the API.

## 2. Scoring rules (shown on every snapshot)
Score = 100 × Σ matching weights ÷ Σ counted weights, one decimal. Only **YES/NO count**; ABSTAIN and ABSENT are excluded. A ballot counts only if the state was a **member on the vote's approved date**. No counted vote → **n/a**, unranked. Ties share a rank (1, 2, 2, 4). A selected vote without a position **blocks creation** (the 400 lists the votes). Inputs are captured when the snapshot is calculated.

## 3. Database and history
- **Approved state:** `votes` and `country_votes`. Each value keeps its provenance (import job, row, approver, time, origin) and a revision.
- **History:** an append-only event log, `vote_history_events`, instead of revision tables. Every change stores old/new value, old/new source (file + row), actor, time, conflict and comment; the vote timeline is a query over it.
- **Import audit:** the stored file (SHA-256), `import_rows` (original text, raw and normalized fields, notes, classification, reason) and `import_vote_outcomes`.
- **Snapshots** copy their inputs and compute results in one SQL statement.
- **PostgreSQL guards:** append-only history; approved rows never deleted, every change bumps the revision; resolved conflicts, finalized imports and completed snapshots immutable; one pending conflict per fact and value; one published snapshot per quarter; no overlapping membership periods.

## 4. Azure
None used, deliberately: everything runs locally with `docker compose up --build`, and the PDF/AI bonus (including its extraction-quality measurement) was not attempted. The mapping is direct: PostgreSQL → Azure Database for PostgreSQL, `IImportFileStorage` → Blob Storage, secrets → Key Vault, logs with correlation id → Application Insights. Cosmos DB does not fit relational, constraint-heavy data.

## 5. Before production
Entra ID/OIDC instead of `X-User-Id`; an audit-table role with INSERT/SELECT only; migrations run from the pipeline; Blob and Key Vault; App Insights with the correlation id sent from the UI; SignalR progress; paging of large reports; backups; an automated full-history snapshot load test (measured manually: ~31 s).

## 6. Next phase with four developers; first standards
Split: (1) **ingestion**: new `IImportFileReader`s, Blob storage, scale-out workers; (2) **review and history**: bulk decisions, audit exports; (3) **index**: bulk positions, snapshot comparison; (4) **frontend and platform**: design system, Playwright, CI/CD, observability.

Standards: the dependency rule, enforced by the existing architecture tests; rule changes start with a failing test; integration tests run on real PostgreSQL; OpenAPI-first with a generated client; ProblemDetails everywhere; migrations and triggers reviewed like code; small PRs with green CI.

## 7. Known limitations
- An unterminated CSV quote merges the following lines into one invalid record (RFC 4180).
- `session` with leading zeros (`079`) is accepted without a note.
- Staging progress shows rows read; the total is known only after staging.
- Snapshot filters combine with AND.
- The UI sends no correlation id; the server creates one per request.
