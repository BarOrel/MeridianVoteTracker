using MeridianVoteTracker.Domain.Common;
using MeridianVoteTracker.Domain.Imports;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Votes;

namespace MeridianVoteTracker.UnitTests.Domain;

public class ImportLifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2024, 4, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static ImportJob NewJob() =>
        ImportJob.Create(Guid.NewGuid(), "q1.csv", "key", new string('a', 64), 100, "csv", "dana", T0, "corr-1");

    [Fact]
    public void Job_goes_through_staging_and_classification_to_completion()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.RecordStagingProgress(5000, T0, Lease);
        job.CompleteStaging(5896);
        job.RecordClassificationProgress(5896, T0, Lease);
        job.Complete(hasIssues: true, T0.AddMinutes(1));

        Assert.Equal(ImportJobStatus.CompletedWithIssues, job.Status);
        Assert.Equal(ImportPhase.Finalized, job.Phase);
        Assert.True(job.IsFinalized);
        Assert.Null(job.LeaseOwner);
    }

    [Fact]
    public void Finalized_job_cannot_change()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.Fail("header missing", T0);

        Assert.Throws<DomainException>(() => job.BeginAttempt("worker-2", T0.AddHours(1), Lease));
        Assert.Throws<DomainException>(() => job.Fail("again", T0));
    }

    [Fact]
    public void Crashed_worker_job_can_be_retaken_only_after_lease_expiry()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.RecordStagingProgress(3000, T0, Lease);

        Assert.Throws<DomainException>(() => job.BeginAttempt("worker-2", T0.AddMinutes(1), Lease));

        job.BeginAttempt("worker-2", T0.AddMinutes(6), Lease);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(ImportPhase.Staging, job.Phase);
        Assert.Equal(0, job.StagedRows); // staging restarts from the file
    }

    [Fact]
    public void Retry_during_classification_resumes_classification()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.CompleteStaging(5896);
        job.RecordClassificationProgress(2000, T0, Lease);

        job.BeginAttempt("worker-2", T0.AddMinutes(6), Lease);

        Assert.Equal(ImportPhase.Classifying, job.Phase);
        Assert.Equal(5896, job.TotalRows);
        Assert.Equal(2000, job.ClassifiedRows);
    }

    [Fact]
    public void Failed_attempt_schedules_a_retry_and_keeps_the_error_visible()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);

        job.RecordFailedAttempt("database unavailable", T0.AddSeconds(15));

        Assert.Equal(ImportJobStatus.Processing, job.Status);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(T0.AddSeconds(15), job.LeaseExpiresAt);
        Assert.Equal("database unavailable", job.Error);
        Assert.False(job.IsOwnedBy("worker-1", 1));

        job.BeginAttempt("worker-1", T0.AddSeconds(16), Lease);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public void Staged_job_can_be_parked_without_a_lease_until_classification_exists()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.CompleteStaging(10);

        job.ParkAwaitingClassification();

        Assert.Equal((ImportJobStatus.Processing, ImportPhase.Classifying), (job.Status, job.Phase));
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
    }

    [Fact]
    public void Ownership_requires_the_same_worker_and_attempt()
    {
        var job = NewJob();
        job.BeginAttempt("worker-1", T0, Lease);
        job.BeginAttempt("worker-2", T0.AddMinutes(6), Lease); // worker-1's lease expired

        Assert.False(job.IsOwnedBy("worker-1", 1));
        Assert.True(job.IsOwnedBy("worker-2", 2));
    }

    [Fact]
    public void Job_gives_up_after_max_attempts()
    {
        var job = NewJob();
        for (var i = 0; i < ImportJob.MaxAttempts; i++)
        {
            job.BeginAttempt($"worker-{i}", T0.AddHours(i), Lease);
        }

        var ex = Assert.Throws<DomainException>(() => job.BeginAttempt("worker-x", T0.AddHours(10), Lease));
        Assert.Equal("import.max_attempts", ex.Code);
    }

    [Fact]
    public void Row_keeps_raw_input_alongside_normalized_values()
    {
        var raw = new ImportRawFields("MA/2024/018", "79", "2024-02-10", "Indigenous Languages", "deu", "Germany", "X", "2024-04-02T09:00:00Z");
        var normalized = new NormalizedImportRecord(AssemblyVoteId.Create("MA/2024/018"), 79, new DateOnly(2024, 2, 10), "Indigenous Languages",
            CountryCode.Create("DEU"), "Germany", Ballot.Absent, T0);

        var row = ImportRow.Staged(Guid.NewGuid(), 2594, "MA/2024/018,79,...,deu,Germany,X,...", raw, normalized, ["country_code: 'deu' → 'DEU' (uppercased)"]);

        Assert.Equal("deu", row.RawCountryCode);
        Assert.Equal(CountryCode.Create("DEU"), row.CountryCode);
        Assert.Single(row.NormalizationNotes);
        Assert.Null(row.Classification);
    }

    [Fact]
    public void Row_is_classified_exactly_once()
    {
        var row = ValidRow();
        row.Classify(ImportClassification.New);

        Assert.Throws<InvalidOperationException>(() => row.Classify(ImportClassification.Unchanged));
        Assert.Equal(ImportClassification.New, row.Classification);
    }

    [Fact]
    public void Invalid_row_is_final_at_staging_and_cannot_be_reclassified()
    {
        var raw = new ImportRawFields("MA/2024/020", "79", "2024-02-14", "Counter-Terrorism", "ARG", "Argentina", "Yes", "2024-04-02T09:00:00Z");
        var row = ImportRow.Invalid(Guid.NewGuid(), 2842, "...", raw, "ballot.invalid", "Ballot must be Y, N, A or X; got 'Yes'.", []);

        Assert.Equal(ImportClassification.Invalid, row.Classification);
        Assert.Equal("Yes", row.RawBallot);
        Assert.Throws<InvalidOperationException>(() => row.Classify(ImportClassification.New));
    }

    [Fact]
    public void Duplicate_records_kind_and_original_row()
    {
        var row = ValidRow();
        row.ClassifyAsDuplicate(896, DuplicateKind.Exact, "Identical to row 896.");

        Assert.Equal(ImportClassification.Duplicate, row.Classification);
        Assert.Equal(896, row.DuplicateOfRowNumber);
        Assert.Equal("duplicate.exact", row.ReasonCode);
    }

    [Fact]
    public void Conflict_classification_must_reference_a_conflict()
    {
        Assert.Throws<InvalidOperationException>(() => ValidRow().Classify(ImportClassification.Conflict));
        Assert.Throws<InvalidOperationException>(() => ValidRow().Classify(ImportClassification.New, conflictId: Guid.NewGuid()));
    }

    private static ImportRow ValidRow()
    {
        var raw = new ImportRawFields("MA/2024/007", "79", "2024-01-22", "Nuclear", "AFG", "Afghanistan", "Y", "2024-04-02T09:00:00Z");
        var normalized = new NormalizedImportRecord(AssemblyVoteId.Create("MA/2024/007"), 79, new DateOnly(2024, 1, 22), "Nuclear",
            CountryCode.Create("AFG"), "Afghanistan", Ballot.Yes, T0);
        return ImportRow.Staged(Guid.NewGuid(), 1045, "...", raw, normalized, []);
    }
}
