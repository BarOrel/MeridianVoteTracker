using MeridianVoteTracker.Application.Imports.GetImportReport;
using MeridianVoteTracker.Application.Imports.GetImportStatus;
using MeridianVoteTracker.Application.Imports.Pipeline;
using MeridianVoteTracker.Application.Imports.ProcessImport;
using MeridianVoteTracker.Application.Imports.StartImport;
using MeridianVoteTracker.Application.Users.GetUser;
using MeridianVoteTracker.Application.Users.ListUsers;
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
        return services;
    }
}
