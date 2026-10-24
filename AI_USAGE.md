# AI usage

## Tools
- **Claude Code** (Anthropic, in VS Code) was the main pair programmer for the whole assignment: reading the brief and the data, planning, writing code and tests, and running builds, tests and Docker.
- Work happened in small phases. Each phase ended with a build, the full test suite and a commit, and I reviewed the result before allowing the next phase.

## How I directed it
- I asked for a plan before any code, then approved it phase by phase (skeleton → domain/persistence → ingestion → classification → conflicts → UI → snapshots).
- Corrections I made to the AI's plan or code:
  - **Persistence.** Its first design leaned towards several thin repository interfaces. I replaced them with a single `IApplicationDbContext` plus a few specialized ports for SQL-specific work, and asked for a safeguard so ordinary use cases cannot write approved data. The result is the read-only `IQueryable`s, the `SaveChanges` guard and the DB triggers.
  - **Concurrency.** Its first plan only conditionally updated the conflict row. I pointed out that two *different* conflicts on the same country vote could still lose an update. The approved value now also carries a revision, checked in the same transaction, and other proposals are superseded.
  - **Raw vs normalized data.** I required the original input to be kept separately from normalized values, and that exact duplicates be distinguished from rows that only become equal after normalization.
  - **Audit.** I asked that every approved change record previous/new value, source file and row, actor and time, and that audit rows be protected against modification.
  - **Scope.** I fixed the time budget and dropped the PDF/Azure AI bonus.

## Where the AI's output was wrong or had to be verified
- **Expected results were not trusted to the AI's own code.** Before writing the classifier, the expected numbers for q1 → rev2 → rev2 were derived with a separate, independent script, and the integration test asserts them exactly.
- **Data profiling mistake.** An early profiling script reported 9 bad ballots in the historic file. Comparing with the system's 5 showed the script had double-counted 4 rows with shifted columns. The system was right.
- **EF Core statement order.** A test caught the job being finalized before its rows were saved in the same `SaveChanges`, so the trigger correctly rejected the row writes. Fixed with an explicit rule: finalization is always the last write.
- **Guard scope bug.** The first version of the restricted-write scope closed before the unit of work saved, which would have broken seeding. It was fixed so that restricted writers flush inside their scope.
- **Memory.** The "bounded memory" claim was measured, not assumed. It showed ~590 MB with Server GC (a GC reservation, not a leak), hence Workstation GC (~110 MB).
- **Tooling slips.** One file edit silently failed because of a Windows encoding issue and left the README status stale; it was found and fixed. Several shell heredocs failed, and files were written with the editor tool instead.

## What I checked myself
- The classification rules against the brief's worked example (§7.2) and against the real files.
- That every interface has a concrete reason to exist.
- Conflict resolution and the HTTP 409 behaviour (a second or concurrent decision on the same conflict), verified with automated integration tests and HTTP requests against the running stack, not manually in two browsers.
- That the published snapshot does not change after a correction.
