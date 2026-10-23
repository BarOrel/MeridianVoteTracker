using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MeridianVoteTracker.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef migrations add`, which does not connect to a database.
/// For `dotnet ef database update`, set MERIDIAN_DESIGN_CONNECTION; no credentials live in code.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MERIDIAN_DESIGN_CONNECTION") ?? "Host=localhost;Database=meridian";
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureDbContext(options, connectionString);
        return new AppDbContext(options.Options);
    }
}
