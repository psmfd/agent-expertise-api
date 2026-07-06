using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-012 acceptance for the backup/restore CLI verbs:
///   1. Round-trip — backup → wipe → restore preserves Id/CreatedAt/UpdatedAt
///      (explicit values win over gen_random_uuid()/now() column defaults —
///      the load-bearing invariant), all content fields, review metadata,
///      embeddings, and the audit log verbatim.
///   2. Quarantine — a record whose content was tampered (stored RecordHash
///      no longer matches) imports as Draft with a RestoreQuarantined audit row.
///   3. Fail closed — a consistently-tampered payload (record + hash both
///      changed, so the Merkle root disagrees with the manifest) aborts with
///      exit 1 and imports nothing.
///   4. Replace-mode preconditions — non-empty target refuses with exit 1.
///   5. Backup refuses to overwrite existing output files.
///
/// Owns its container (same rationale as MigrateCommandTests) but migrates to
/// HEAD in InitializeAsync — these tests exercise the verbs, not migration.
/// </summary>
public class BackupRestoreCommandTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("backup_cmd")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync().AsTask();

    [Fact]
    public async Task BackupThenRestore_RoundTripsEntriesAuditAndEmbeddingMetadata()
    {
        var seeded = await SeedAsync();
        var dir = FreshDir();

        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir, "--instance-id", "test-instance"]))
            .Should().Be(0);
        await WipeAsync();

        (await RestoreCommand.RunAsync(BuildApp(), ["restore", "--input", dir]))
            .Should().Be(0);

        await using var db = NewContext();
        var restored = await db.ExpertiseEntries.IgnoreQueryFilters()
            .OrderBy(e => e.Id).AsNoTracking().ToListAsync();
        restored.Should().HaveCount(seeded.Count);

        foreach (var original in seeded)
        {
            var r = restored.Single(e => e.Id == original.Id);
            r.CreatedAt.Should().Be(original.CreatedAt, "explicit CreatedAt must win over the now() column default");
            r.UpdatedAt.Should().Be(original.UpdatedAt);
            r.Title.Should().Be(original.Title);
            r.Body.Should().Be(original.Body);
            r.Tenant.Should().Be(original.Tenant);
            r.ReviewState.Should().Be(original.ReviewState);
            r.ReviewedBy.Should().Be(original.ReviewedBy);
            r.IntegrityHash.Should().Be(original.IntegrityHash);
            r.Tags.Should().BeEquivalentTo(original.Tags);
        }

        restored.Single(e => e.Title == "approved entry").Embedding.Should().NotBeNull(
            "embeddings ride along in the artifact for restore speed");

        var audit = await db.ExpertiseAuditLogs.AsNoTracking().ToListAsync();
        audit.Should().HaveCount(seeded.Count, "the audit log is restored verbatim, no new rows for a clean restore");

        var metadata = await db.EmbeddingMetadata.SingleAsync();
        metadata.ModelName.Should().Be("bge-micro-v2");
        metadata.Dimensions.Should().Be(384);
    }

    [Fact]
    public async Task Restore_ContentTamperedRecord_QuarantinesAsDraftWithAuditRow()
    {
        var seeded = await SeedAsync();
        var target = seeded.Single(e => e.Title == "approved entry");
        var dir = FreshDir();

        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(0);
        await WipeAsync();

        // Tamper the CONTENT only — the stored recordHash goes stale, which is
        // exactly the per-record quarantine case (root still matches, because
        // the root covers the stored hashes).
        var entriesPath = Path.Combine(dir, "entries.jsonl");
        var tampered = (await File.ReadAllTextAsync(entriesPath))
            .Replace("approved body", "poisoned body", StringComparison.Ordinal);
        await File.WriteAllTextAsync(entriesPath, tampered);

        (await RestoreCommand.RunAsync(BuildApp(), ["restore", "--input", dir])).Should().Be(0);

        await using var db = NewContext();
        var quarantinedEntry = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == target.Id);
        quarantinedEntry.ReviewState.Should().Be(ReviewState.Draft);
        quarantinedEntry.ReviewedBy.Should().BeNull("quarantine clears review metadata");
        quarantinedEntry.Body.Should().Be("poisoned body", "content is imported, just not trusted as Approved");

        var quarantineRow = await db.ExpertiseAuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditAction.RestoreQuarantined);
        quarantineRow.EntryId.Should().Be(target.Id);
        quarantineRow.Principal.Should().Be("restore-cli");
        quarantineRow.ActorClass.Should().Be(ActorClass.Service);

        // The untouched entries keep their review state.
        var untouched = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Title == "draft entry");
        untouched.ReviewState.Should().Be(ReviewState.Draft);
    }

    [Fact]
    public async Task Restore_ConsistentlyTamperedPayload_FailsClosedAndImportsNothing()
    {
        await SeedAsync();
        var dir = FreshDir();

        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(0);
        await WipeAsync();

        // Tamper record AND recompute its stored hash: every per-record check
        // passes, but the Merkle root no longer matches the manifest — the
        // manifest (signed in production) is the trust anchor, so this must
        // abort entirely, not quarantine.
        var entriesPath = Path.Combine(dir, "entries.jsonl");
        var lines = await File.ReadAllLinesAsync(entriesPath);
        var record = JsonSerializer.Deserialize(lines[0], BackupJsonContext.Default.BackupEntryRecord)!;
        record = record with { Body = "silently rewritten" };
        record = record with { RecordHash = BackupRecordHash.ComputeEntry(record) };
        lines[0] = JsonSerializer.Serialize(record, BackupJsonContext.Default.BackupEntryRecord);
        await File.WriteAllLinesAsync(entriesPath, lines);

        (await RestoreCommand.RunAsync(BuildApp(), ["restore", "--input", dir])).Should().Be(1);

        await using var db = NewContext();
        (await db.ExpertiseEntries.IgnoreQueryFilters().AnyAsync()).Should().BeFalse(
            "fail-closed validation happens before any row is written");
    }

    [Fact]
    public async Task Restore_ForceDraft_LandsEveryEntryAsDraft()
    {
        await SeedAsync();
        var dir = FreshDir();

        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(0);
        await WipeAsync();

        (await RestoreCommand.RunAsync(BuildApp(), ["restore", "--input", dir, "--force-draft"]))
            .Should().Be(0);

        await using var db = NewContext();
        var states = await db.ExpertiseEntries.IgnoreQueryFilters().AsNoTracking()
            .Select(e => e.ReviewState).ToListAsync();
        states.Should().OnlyContain(s => s == ReviewState.Draft,
            "--force-draft (foreign-backup seed) re-gates everything behind local review");
    }

    [Fact]
    public async Task Restore_NonEmptyTarget_ReturnsOne()
    {
        await SeedAsync();
        var dir = FreshDir();
        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(0);

        // Target NOT wiped — replace mode must refuse.
        (await RestoreCommand.RunAsync(BuildApp(), ["restore", "--input", dir])).Should().Be(1);
        await WipeAsync();
    }

    [Fact]
    public async Task Backup_ExistingOutputFiles_ReturnsOne()
    {
        await SeedAsync();
        var dir = FreshDir();

        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(0);
        (await BackupCommand.RunAsync(BuildApp(), ["backup", "--output", dir])).Should().Be(1,
            "never overwrite an existing backup payload silently");
        await WipeAsync();
    }

    // ---- Helpers ---------------------------------------------------------

    private async Task<List<ExpertiseEntry>> SeedAsync()
    {
        await using var db = NewContext();

        var draft = new ExpertiseEntry
        {
            Domain = "shared",
            Title = "draft entry",
            Body = "draft body",
            EntryType = EntryType.Caveat,
            Severity = Severity.Warning,
            Source = "human",
            Tags = ["one", "two"],
            Tenant = "team-alpha",
            AuthorPrincipal = "author@example.com",
            ReviewState = ReviewState.Draft,
        };
        var approved = new ExpertiseEntry
        {
            Domain = "shared",
            Title = "approved entry",
            Body = "approved body",
            EntryType = EntryType.Pattern,
            Severity = Severity.Info,
            Source = "agent",
            SourceVersion = "1.0",
            Tenant = "shared",
            Visibility = Visibility.Shared,
            AuthorPrincipal = "author@example.com",
            AuthorAgent = "claude-code",
            ReviewState = ReviewState.Approved,
            ReviewedBy = "reviewer@example.com",
            ReviewedAt = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            Embedding = new Vector(Enumerable.Repeat(0.5f, 384).ToArray()),
        };
        var deprecated = new ExpertiseEntry
        {
            Domain = "dotnet",
            Title = "deprecated entry",
            Body = "old advice",
            EntryType = EntryType.IssueFix,
            Severity = Severity.Critical,
            Source = "human",
            Tenant = "team-alpha",
            AuthorPrincipal = "author@example.com",
            ReviewState = ReviewState.Rejected,
            RejectionReason = "superseded",
            DeprecatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var entries = new List<ExpertiseEntry> { draft, approved, deprecated };
        foreach (var e in entries)
        {
            e.IntegrityHash = IntegrityHashService.Compute(e);
            db.ExpertiseEntries.Add(e);
        }

        db.EmbeddingMetadata.Add(new EmbeddingMetadata
        {
            ModelName = "bge-micro-v2",
            Dimensions = 384,
            LastReembedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        foreach (var e in entries)
        {
            db.ExpertiseAuditLogs.Add(new ExpertiseAuditLog
            {
                Action = AuditAction.Created,
                EntryId = e.Id,
                Tenant = e.Tenant,
                Principal = e.AuthorPrincipal,
                AfterHash = e.IntegrityHash,
                ActorClass = ActorClass.Human,
                AuthMethod = "LocalDev",
            });
        }
        await db.SaveChangesAsync();

        // Re-read so the returned snapshot carries the server-generated
        // Id/CreatedAt/UpdatedAt values the round-trip assertions compare against.
        return await db.ExpertiseEntries.IgnoreQueryFilters()
            .OrderBy(e => e.Id).AsNoTracking().ToListAsync();
    }

    private async Task WipeAsync()
    {
        await using var db = NewContext();
        await db.ExpertiseAuditLogs.ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.EmbeddingMetadata.ExecuteDeleteAsync();
    }

    private static string FreshDir()
    {
        // Path.Join (not Path.Combine) — Join never drops earlier segments if a
        // later one looks rooted (CodeQL cs/path-combine on the 3-arg Combine).
        var dir = Path.Join(Path.GetTempPath(), "backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(_container.GetConnectionString(), o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    private WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        builder.Services.AddDbContext<ExpertiseDbContext>(options =>
            options.UseNpgsql(_container.GetConnectionString(), o => o.UseVector()));
        return builder.Build();
    }
}
