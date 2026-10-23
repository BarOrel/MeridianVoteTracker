using System.Reflection;

namespace MeridianVoteTracker.UnitTests;

/// <summary>
/// Guards the Clean Architecture dependency rule at the assembly level, so a stray
/// "using Microsoft.EntityFrameworkCore" in Domain or Application fails the build pipeline.
/// </summary>
public class ArchitectureTests
{
    private static readonly string[] InfrastructureConcerns =
    [
        "MeridianVoteTracker.Infrastructure",
        "MeridianVoteTracker.Api",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "CsvHelper",
        "Microsoft.AspNetCore",
    ];

    [Fact]
    public void Domain_references_no_other_project_or_infrastructure_package()
    {
        var references = ReferencedAssemblyNames("MeridianVoteTracker.Domain");

        Assert.DoesNotContain(references, name => name.StartsWith("MeridianVoteTracker.", StringComparison.Ordinal));
        AssertNoInfrastructureConcerns(references);
    }

    [Fact]
    public void Application_does_not_reference_infrastructure_or_api()
    {
        AssertNoInfrastructureConcerns(ReferencedAssemblyNames("MeridianVoteTracker.Application"));
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
