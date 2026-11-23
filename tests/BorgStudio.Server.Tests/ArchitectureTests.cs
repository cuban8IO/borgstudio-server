using System.Reflection;

using BorgStudio.Server.Domain;

namespace BorgStudio.Server.Tests;

/// <summary>Clean Architecture: dependencies only point inwards (Web → Infrastructure → Application → Domain).</summary>
public sealed class ArchitectureTests
{
    private const string Domain = "BorgStudio.Server.Domain";
    private const string Application = "BorgStudio.Server.Application";
    private const string Infrastructure = "BorgStudio.Server.Infrastructure";
    private const string Web = "BorgStudio.Server";

    private static readonly Assembly DomainAssembly = typeof(ProductVersion).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Server.Application.DependencyInjection).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Server.Infrastructure.DependencyInjection).Assembly;

    [Fact]
    public void AssemblyNames_AreAsExpected()
    {
        Assert.Equal(Domain, DomainAssembly.GetName().Name);
        Assert.Equal(Application, ApplicationAssembly.GetName().Name);
        Assert.Equal(Infrastructure, InfrastructureAssembly.GetName().Name);
        Assert.Equal(Web, typeof(Program).Assembly.GetName().Name);
    }

    [Fact]
    public void Domain_DependsOnNothingElse()
    {
        AssertDoesNotReference(DomainAssembly, Application, Infrastructure, Web);
        AssertDoesNotReferencePrefix(DomainAssembly, "Microsoft.");
    }

    [Fact]
    public void Application_DependsOnlyOnDomain()
    {
        AssertDoesNotReference(ApplicationAssembly, Infrastructure, Web);
        AssertDoesNotReferencePrefix(ApplicationAssembly, "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "MudBlazor");
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnWeb()
    {
        AssertDoesNotReference(InfrastructureAssembly, Web);
        AssertDoesNotReferencePrefix(InfrastructureAssembly, "Microsoft.AspNetCore.Components", "MudBlazor");
    }

    private static void AssertDoesNotReference(Assembly assembly, params string[] forbidden)
    {
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, references);
        }
    }

    private static void AssertDoesNotReferencePrefix(Assembly assembly, params string[] forbiddenPrefixes)
    {
        var offending = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(offending);
    }
}
