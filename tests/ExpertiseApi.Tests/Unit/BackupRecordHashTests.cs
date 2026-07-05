using ExpertiseApi.Cli;
using ExpertiseApi.Services;

namespace ExpertiseApi.Tests.Unit;

public class BackupRecordHashTests
{
    private static BackupEntryRecord Entry(Action<Builder>? mutate = null)
    {
        var b = new Builder();
        mutate?.Invoke(b);
        return b.Build();
    }

    private sealed class Builder
    {
        public Guid Id { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public IReadOnlyList<string> Tags { get; set; } = ["beta", "alpha"];
        public string Body { get; set; } = "body";
        public string? ReviewedBy { get; set; }
        public DateTime CreatedAt { get; set; } = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        public IReadOnlyList<float>? Embedding { get; set; }
        public string RecordHash { get; set; } = "";

        public BackupEntryRecord Build() => new()
        {
            Id = Id,
            Domain = "shared",
            Tags = Tags,
            Title = "title",
            Body = Body,
            EntryType = "Pattern",
            Severity = "Info",
            Source = "human",
            Embedding = Embedding,
            CreatedAt = CreatedAt,
            UpdatedAt = new DateTime(2026, 7, 5, 13, 0, 0, DateTimeKind.Utc),
            Tenant = "team-alpha",
            Visibility = "Private",
            AuthorPrincipal = "user@example.com",
            ReviewedBy = ReviewedBy,
            ReviewState = "Draft",
            RecordHash = RecordHash,
        };
    }

    [Fact]
    public void Deterministic_ForIdenticalRecords()
    {
        BackupRecordHash.ComputeEntry(Entry()).Should().Be(BackupRecordHash.ComputeEntry(Entry()));
    }

    [Fact]
    public void TagStorageOrder_DoesNotAffectHash()
    {
        var a = Entry(b => b.Tags = ["beta", "alpha"]);
        var b = Entry(x => x.Tags = ["alpha", "beta"]);
        BackupRecordHash.ComputeEntry(a).Should().Be(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void RecordHashField_IsNotPartOfTheHash()
    {
        var a = Entry(b => b.RecordHash = "");
        var b = Entry(x => x.RecordHash = "something-else");
        BackupRecordHash.ComputeEntry(a).Should().Be(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void Embedding_IsOutsideTheTrustBoundary()
    {
        // ADR-012: embeddings are derived data — swapping them must not change the hash.
        var a = Entry(b => b.Embedding = null);
        var b = Entry(x => x.Embedding = new float[] { 1.0f, 2.0f });
        BackupRecordHash.ComputeEntry(a).Should().Be(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void ContentChange_ChangesHash()
    {
        var a = Entry();
        var b = Entry(x => x.Body = "tampered");
        BackupRecordHash.ComputeEntry(a).Should().NotBe(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void IdChange_ChangesHash()
    {
        // The hash binds record identity: identical content under a different Id
        // must not be swappable.
        var a = Entry();
        var b = Entry(x => x.Id = Guid.Parse("22222222-2222-2222-2222-222222222222"));
        BackupRecordHash.ComputeEntry(a).Should().NotBe(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void NullAndAbsentString_AreDistinctFromEmpty()
    {
        var a = Entry(b => b.ReviewedBy = null);
        var b = Entry(x => x.ReviewedBy = "");
        BackupRecordHash.ComputeEntry(a).Should().NotBe(BackupRecordHash.ComputeEntry(b));
    }

    [Fact]
    public void TimestampKind_IsNormalizedToUtc()
    {
        // Same instant expressed as UTC and as local time must hash identically —
        // Npgsql may materialize either depending on context.
        var utc = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        var a = Entry(b => b.CreatedAt = utc);
        var b = Entry(x => x.CreatedAt = utc.ToLocalTime());
        BackupRecordHash.ComputeEntry(a).Should().Be(BackupRecordHash.ComputeEntry(b));
    }
}
