using System.Reflection;
using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Domain.Imports;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.UnitTests;

/// <summary>
/// Guards the dependency rule at the assembly level. Accepted tradeoff: Application may use EF Core
/// abstractions (DbSet, async LINQ) but never a provider or infrastructure package.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] InfrastructureConcerns =
    [
        "MeridianVoteTracker.Infrastructure",
        "MeridianVoteTracker.Api",
        "Microsoft.EntityFrameworkCore.Relational",
        "Npgsql",
        "CsvHelper",
        "Microsoft.AspNetCore",
    ];

    /// <summary>Entity types use cases may write directly; everything else needs a dedicated port.</summary>
    private static readonly Type[] OrdinaryWritableTypes = [typeof(ImportJob), typeof(ImportRow)];

    [Fact]
    public void Domain_references_no_other_project_and_no_persistence_framework()
    {
        var references = ReferencedAssemblyNames("MeridianVoteTracker.Domain");

        Assert.DoesNotContain(references, name => name.StartsWith("MeridianVoteTracker.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        AssertNoInfrastructureConcerns(references);
    }

    [Fact]
    public void Application_uses_ef_core_abstractions_but_no_provider_infrastructure_or_api()
    {
        AssertNoInfrastructureConcerns(ReferencedAssemblyNames("MeridianVoteTracker.Application"));
    }

    [Fact]
    public void Application_db_context_exposes_DbSet_only_for_ordinary_workflow_entities()
    {
        var properties = typeof(IApplicationDbContext).GetProperties();

        var writable = properties
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0]);
        Assert.Equal(OrdinaryWritableTypes.OrderBy(t => t.Name), writable.OrderBy(t => t.Name));

        var readOnly = properties.Where(p => !OrdinaryWritableTypes.Contains(p.PropertyType.GetGenericArguments()[0]));
        Assert.All(readOnly, p => Assert.Equal(typeof(IQueryable<>), p.PropertyType.GetGenericTypeDefinition()));
    }

    [Fact]
    public void Application_db_context_cannot_commit_on_its_own()
    {
        Assert.DoesNotContain(typeof(IApplicationDbContext).GetMethods(), m => m.Name.StartsWith("SaveChanges", StringComparison.Ordinal));
    }

    private static string[] ReferencedAssemblyNames(string assemblyName) =>
        Assembly.Load(assemblyName).GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    private static void AssertNoInfrastructureConcerns(string[] references)
    {
        foreach (var forbidden in InfrastructureConcerns)
        {
            Assert.DoesNotContain(references, name => name.StartsWith(forbidden, StringComparison.Ordinal));
        }
    }
}
