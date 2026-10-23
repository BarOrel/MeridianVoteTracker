using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.MemberStates;
using MeridianVoteTracker.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeridianVoteTracker.Infrastructure.Persistence.Seed;

internal sealed record SeedResult(int StatesAdded, int PeriodsAdded, int UsersAdded);

/// <summary>
/// Applies migrations and seeds reference data (member states, users) at start-up.
/// Idempotent: only missing states, periods and users are inserted, inside one transaction, so a
/// restart (or a second run) changes nothing and a failure leaves no partial seed. Existing rows
/// are never modified: if the file's name for a code differs from the stored one, the stored name
/// is kept and a warning logged (the code is the identity, ING-11).
/// </summary>
internal sealed class DatabaseInitializer(AppDbContext db, IUnitOfWork unitOfWork, ILogger<DatabaseInitializer> logger)
{
    private const string MemberStatesResource = "MeridianVoteTracker.Seed.member_states.csv";

    private static readonly AppUser[] SeedUsers =
    [
        new("dana", "Dana", UserRole.Analyst),
        new("omar", "Omar", UserRole.Analyst),
        new("lena", "Lena", UserRole.Admin),
    ];

    public async Task<SeedResult> InitializeAsync(CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        var result = await unitOfWork.ExecuteInTransactionAsync(SeedAsync, cancellationToken);
        logger.LogInformation("Seed completed: {StatesAdded} states, {PeriodsAdded} membership periods, {UsersAdded} users added",
            result.StatesAdded, result.PeriodsAdded, result.UsersAdded);
        return result;
    }

    private async Task<SeedResult> SeedAsync(CancellationToken cancellationToken)
    {
        // Reference data is a restricted entity type; the seeder is one of its dedicated writers.
        using var _ = db.AllowRestrictedWrites();
        var seedRows = ReadMemberStates();
        var existing = await db.MemberStates.Include(s => s.Periods).ToDictionaryAsync(s => s.Code, cancellationToken);
        int statesAdded = 0, periodsAdded = 0;

        foreach (var row in seedRows)
        {
            var code = CountryCode.Create(row.Code);
            if (!existing.TryGetValue(code, out var state))
            {
                state = new MemberState(code, row.Name);
                existing.Add(code, state);
                db.MemberStates.Add(state);
                statesAdded++;
            }
            else if (state.Name != row.Name.Trim())
            {
                logger.LogWarning("member_states.csv line {Line}: name '{FileName}' differs from stored '{StoredName}' for {Code}; stored name kept",
                    row.LineNumber, row.Name, state.Name, code);
            }

            if (state.AddPeriod(row.From, row.To))
            {
                periodsAdded++;
            }
        }

        var existingUserIds = await db.Users.Select(u => u.Id).ToListAsync(cancellationToken);
        var missingUsers = SeedUsers.Where(u => !existingUserIds.Contains(u.Id)).ToList();
        db.Users.AddRange(missingUsers);

        // Restricted writers flush inside their scope (and inside the open transaction);
        // the unit of work still owns the commit.
        await db.SaveChangesAsync(cancellationToken);
        return new SeedResult(statesAdded, periodsAdded, missingUsers.Count);
    }

    private static IReadOnlyList<MemberStateSeedRow> ReadMemberStates()
    {
        using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream(MemberStatesResource)
            ?? throw new InvalidOperationException($"Embedded resource '{MemberStatesResource}' not found.");
        using var reader = new StreamReader(stream);
        return MemberStatesCsv.Parse(reader);
    }
}
