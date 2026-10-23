using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Infrastructure.Persistence;
using MeridianVoteTracker.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
