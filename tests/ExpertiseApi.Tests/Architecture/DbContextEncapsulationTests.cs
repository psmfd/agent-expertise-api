using ExpertiseApi.Data;

namespace ExpertiseApi.Tests.Architecture;

public class DbContextEncapsulationTests
{
    [Fact]
    public void NonRepositoryCode_ShouldNotDependOnDbContextViaConstructor()
    {
        // Only Data/ types may take ExpertiseDbContext as a constructor parameter.
        // Cli/ types (e.g. ReembedCommand, RehashCommand) resolve the context via
        // IServiceProvider directly and are intentionally exempt from this rule.
        var assembly = typeof(ExpertiseDbContext).Assembly;

        var violations = assembly.GetTypes()
            .Where(t => t.Namespace is not null
                        && t.Namespace.StartsWith("ExpertiseApi.")
                        && !t.Namespace.StartsWith("ExpertiseApi.Data")
                        && !t.Namespace.StartsWith("ExpertiseApi.Cli")
                        && !t.Namespace.StartsWith("ExpertiseApi.Migrations"))
            .Where(t => t.GetConstructors()
                         .SelectMany(c => c.GetParameters())
                         .Any(p => p.ParameterType == typeof(ExpertiseDbContext)))
            .Select(t => t.FullName)
            .ToList();

        violations.Should().BeEmpty(
            "only Data/ layer types may take ExpertiseDbContext as a constructor parameter; " +
            "everything else must consume IExpertiseRepository instead");
    }
}
