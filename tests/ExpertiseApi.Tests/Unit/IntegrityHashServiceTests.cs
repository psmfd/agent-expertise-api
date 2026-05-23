using ExpertiseApi.Models;
using ExpertiseApi.Services;

namespace ExpertiseApi.Tests.Unit;

public class IntegrityHashServiceTests
{
    [Fact]
    public void Compute_IsLowercaseHex_64Chars()
    {
        var hash = IntegrityHashService.Compute(
            tenant: "team-alpha",
            title: "Title",
            body: "Body",
            entryType: EntryType.Pattern,
            severity: Severity.Info);

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_IsDeterministic_AcrossInvocations()
    {
        var first = IntegrityHashService.Compute("t", "title", "body", EntryType.Pattern, Severity.Info);
        var second = IntegrityHashService.Compute("t", "title", "body", EntryType.Pattern, Severity.Info);

        first.Should().Be(second);
    }

    [Fact]
    public void Compute_DiffersByTenant()
    {
        var a = IntegrityHashService.Compute("tenant-a", "title", "body", EntryType.Pattern, Severity.Info);
        var b = IntegrityHashService.Compute("tenant-b", "title", "body", EntryType.Pattern, Severity.Info);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_DiffersByTitle()
    {
        var a = IntegrityHashService.Compute("t", "title-a", "body", EntryType.Pattern, Severity.Info);
        var b = IntegrityHashService.Compute("t", "title-b", "body", EntryType.Pattern, Severity.Info);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_DiffersByBody()
    {
        var a = IntegrityHashService.Compute("t", "title", "body-a", EntryType.Pattern, Severity.Info);
        var b = IntegrityHashService.Compute("t", "title", "body-b", EntryType.Pattern, Severity.Info);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_DiffersByEntryType()
    {
        var a = IntegrityHashService.Compute("t", "title", "body", EntryType.Pattern, Severity.Info);
        var b = IntegrityHashService.Compute("t", "title", "body", EntryType.IssueFix, Severity.Info);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_DiffersBySeverity()
    {
        var a = IntegrityHashService.Compute("t", "title", "body", EntryType.Pattern, Severity.Info);
        var b = IntegrityHashService.Compute("t", "title", "body", EntryType.Pattern, Severity.Critical);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_IgnoresFieldsNotInCanonicalForm()
    {
        // SourceVersion, Source, AuthorPrincipal, Visibility, ReviewState, etc. are NOT
        // part of the integrity hash — only {tenant, title, body, entryType, severity} are.
        var entry1 = new ExpertiseEntry
        {
            Domain = "shared",
            Tenant = "t",
            Title = "title",
            Body = "body",
            EntryType = EntryType.Pattern,
            Severity = Severity.Info,
            Source = "agent-a",
            SourceVersion = "v1",
            AuthorPrincipal = "p1",
            Visibility = Visibility.Private,
            ReviewState = ReviewState.Draft
        };
        var entry2 = new ExpertiseEntry
        {
            Domain = "different-domain",
            Tenant = "t",
            Title = "title",
            Body = "body",
            EntryType = EntryType.Pattern,
            Severity = Severity.Info,
            Source = "agent-b",
            SourceVersion = "v99",
            AuthorPrincipal = "p2",
            Visibility = Visibility.Shared,
            ReviewState = ReviewState.Approved
        };

        IntegrityHashService.Compute(entry1).Should().Be(IntegrityHashService.Compute(entry2));
    }

    [Fact]
    public void Compute_OverloadFromEntry_MatchesScalarOverload()
    {
        var entry = new ExpertiseEntry
        {
            Domain = "shared",
            Tenant = "team-alpha",
            Title = "Title",
            Body = "Body",
            EntryType = EntryType.Caveat,
            Severity = Severity.Warning,
            Source = "human",
            AuthorPrincipal = "test"
        };

        var fromEntry = IntegrityHashService.Compute(entry);
        var fromScalar = IntegrityHashService.Compute(
            "team-alpha", "Title", "Body", EntryType.Caveat, Severity.Warning);

        fromEntry.Should().Be(fromScalar);
    }
}
