using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Imports;
using MeridianVoteTracker.Application.Imports.Ports;
using MeridianVoteTracker.Application.Snapshots.Ports;
using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Infrastructure.BackgroundJobs;
using MeridianVoteTracker.Infrastructure.Imports;
using MeridianVoteTracker.Infrastructure.Imports.Csv;
using MeridianVoteTracker.Infrastructure.Persistence;
using MeridianVoteTracker.Infrastructure.Persistence.Seed;
using MeridianVoteTracker.Infrastructure.Persistence.Snapshots;
using MeridianVoteTracker.Infrastructure.Persistence.Votes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeridianVoteTracker.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // One pooled NpgsqlDataSource for the whole process. EF Core and the binary COPY staging
        // writer (Phase 2) share it. Resolved lazily so the connection string can come from env vars,
        // user-secrets or test overrides.
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured. " +
                    $"Set ConnectionStrings__{ConnectionStringName} or use dotnet user-secrets.");
            }

            return NpgsqlDataSource.Create(connectionString);
        });

        services.AddDbContext<AppDbContext>((sp, options) => ConfigureDbContext(options, sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton(TimeProvider.System);
        // Same scoped instance behind both interfaces: use-case queries/changes and the unit of work
        // share one change tracker and one transaction.
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<DatabaseInitializer>();

        // Imports: format readers (add new formats here), storage, specialized persistence ports, worker.
        services.AddSingleton<IImportFileReader, CsvImportFileReader>();
        services.AddOptions<ImportStorageOptions>().BindConfiguration(ImportStorageOptions.SectionName);
        services.AddSingleton<IImportFileStorage, LocalDiskImportFileStorage>();
        services.AddScoped<IImportStagingStore, PostgresImportStagingStore>();
        services.AddScoped<IImportJobStore, PostgresImportJobStore>();
        services.AddScoped<IApprovedVoteStore, PostgresApprovedVoteStore>();
        services.AddOptions<ImportWorkerOptions>().BindConfiguration(ImportWorkerOptions.SectionName);
        services.AddSingleton(sp =>
        {
            var worker = sp.GetRequiredService<IOptions<ImportWorkerOptions>>().Value;
            return new ImportProcessingSettings(worker.BatchSize, worker.LeaseDuration, worker.RetryDelay, worker.VotesPerClassificationBatch);
        });
        services.AddHostedService<ImportWorker>();
        services.AddScoped<ISnapshotCalculator, PostgresSnapshotCalculator>();
        services.AddHostedService<SnapshotWorker>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckTags.Ready]);

        return services;
    }

    /// <summary>Applies migrations and idempotently seeds reference data. Called once at start-up.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(cancellationToken);
    }

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, NpgsqlDataSource dataSource) =>
        options.UseNpgsql(dataSource, ConfigureNpgsql).UseSnakeCaseNamingConvention();

    internal static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, ConfigureNpgsql).UseSnakeCaseNamingConvention();

    private static void ConfigureNpgsql(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder npgsql) =>
        npgsql.MigrationsHistoryTable("__ef_migrations_history");
}

public static class HealthCheckTags
{
    public const string Ready = "ready";
}
