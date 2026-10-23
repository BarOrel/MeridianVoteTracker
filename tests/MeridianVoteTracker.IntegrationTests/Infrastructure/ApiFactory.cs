using MeridianVoteTracker.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace MeridianVoteTracker.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL container (real database, not an in-memory fake).
/// Shared per test collection to pay the container start-up cost once. The import worker is off here,
/// so jobs created directly by persistence tests are never picked up; see <see cref="ImportApiFactory"/>.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "meridian-tests", Guid.NewGuid().ToString("N"));

    protected virtual bool ImportWorkerEnabled => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting($"ConnectionStrings:{DependencyInjection.ConnectionStringName}", ConnectionString);
        builder.UseSetting("ImportStorage:RootPath", StorageRoot);
        builder.UseSetting("ImportWorker:Enabled", ImportWorkerEnabled ? "true" : "false");
        builder.UseSetting("ImportWorker:PollInterval", "00:00:00.200");
        builder.UseSetting("ImportWorker:BatchSize", "1000"); // several batches even for small files
        builder.UseSetting("ImportWorker:VotesPerClassificationBatch", "50");
    }

    public Task InitializeAsync() => _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}

/// <summary>Same API with the background import worker running, on its own database.</summary>
public sealed class ImportApiFactory : ApiFactory
{
    protected override bool ImportWorkerEnabled => true;
}

/// <summary>Own database for the q1 → rev2 → rev2 scenario, whose expected numbers assume no prior approved data.</summary>
public sealed class RealFilesApiFactory : ApiFactory
{
    protected override bool ImportWorkerEnabled => true;
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

[CollectionDefinition(Name)]
public sealed class ImportCollection : ICollectionFixture<ImportApiFactory>
{
    public const string Name = "imports";
}

[CollectionDefinition(Name)]
public sealed class RealFilesCollection : ICollectionFixture<RealFilesApiFactory>
{
    public const string Name = "real-files";
}
