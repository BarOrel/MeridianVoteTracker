using MeridianVoteTracker.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MeridianVoteTracker.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        // One pooled NpgsqlDataSource for the whole process. EF Core (Phase 1) and the
        // binary COPY staging writer (Phase 2) will share it. Resolved lazily so the
        // connection string can come from env vars, user-secrets or test overrides.
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

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: [HealthCheckTags.Ready]);

        return services;
    }
}

public static class HealthCheckTags
{
    public const string Ready = "ready";
}
