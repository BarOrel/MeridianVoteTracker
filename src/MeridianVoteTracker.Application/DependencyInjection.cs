using MeridianVoteTracker.Application.Conflicts;
using MeridianVoteTracker.Application.Imports.GetImportReport;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.ProcessImport;
using MeridianVoteTracker.Application.Imports.StartImport;
using MeridianVoteTracker.Application.Snapshots;
using MeridianVoteTracker.Application.Users.GetUser;
using MeridianVoteTracker.Application.Users.ListUsers;
using MeridianVoteTracker.Application.Votes;
using Microsoft.Extensions.DependencyInjection;

namespace MeridianVoteTracker.Application;

public static class DependencyInjection
{
    /// <summary>Registers use cases explicitly (no assembly scanning), so the composition is readable in one place.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ListUsersUseCase>();
        services.AddScoped<GetUserUseCase>();

        services.AddSingleton<ImportFileReaderResolver>();
        services.AddScoped<StartImportUseCase>();
        services.AddScoped<GetImportStatusUseCase>();
        services.AddScoped<GetImportReportUseCase>();
        services.AddScoped<ListImportRowsUseCase>();
        services.AddScoped<ClaimNextImportUseCase>();
        services.AddScoped<ProcessImportUseCase>();
        services.AddScoped<RecordImportFailureUseCase>();
        services.AddScoped<ListImportsUseCase>();

        services.AddScoped<ListConflictsUseCase>();
        services.AddScoped<GetConflictUseCase>();
        services.AddScoped<ResolveConflictUseCase>();

        services.AddScoped<ListVotesUseCase>();
        services.AddScoped<GetVoteUseCase>();
        services.AddScoped<GetVoteTimelineUseCase>();
        services.AddScoped<SetVotePositionUseCase>();

        services.AddScoped<CreateSnapshotUseCase>();
        services.AddScoped<ListSnapshotsUseCase>();
        services.AddScoped<GetSnapshotUseCase>();
        services.AddScoped<GetSnapshotCountryUseCase>();
        services.AddScoped<PublishSnapshotUseCase>();
        services.AddScoped<CalculateNextSnapshotUseCase>();
        return services;
    }
}
