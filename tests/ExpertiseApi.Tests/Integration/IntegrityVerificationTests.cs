using ExpertiseApi.Auth;
using ExpertiseApi.Cli;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-020 PR 2 tamper-detection coverage: the <c>verify</c> verb against real
/// Postgres, with tampering applied the way the threat model says it happens — direct
/// database writes that bypass the repository (and therefore recompute no MAC and
/// write no audit row). Owns its container (CliMaintenanceCommandTests pattern).
/// </summary>
public class IntegrityVerificationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("integrity_verify")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private static readonly IntegrityKeyProvider Keyed = IntegrityKeyProvider.Load(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integrity:HmacKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray()),
            })
            .Build(),
        isDevelopment: true);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Verify_CleanRun_SealsAndChainsCheckpoints_ExitsZero()
    {
        await SeedEntriesWithAudit(3);

        (await RunVerify()).Should().Be(0);

        await using (var db = NewContext())
        {
            var first = await db.AuditCheckpoints.SingleAsync();
            first.PrevCheckpointMac.Should().BeNull("the first link references nothing");
            first.RowCount.Should().Be(3);
            first.CheckpointMac.Should().StartWith("k1:", "sealing uses the active key");

            var state = await db.IntegrityVerificationStates.SingleAsync();
            state.LastResult.Should().Be("clean");
            state.MismatchCount.Should().Be(0);
        }

        // New audit rows after the first seal: the second run must re-verify link 1
        // clean and seal link 2 chained to it.
        await SeedEntriesWithAudit(2);
        (await RunVerify()).Should().Be(0);

        await using (var db = NewContext())
        {
            var checkpoints = await db.AuditCheckpoints.OrderBy(c => c.Id).ToListAsync();
            checkpoints.Should().HaveCount(2);
            checkpoints[1].PrevCheckpointMac.Should().Be(checkpoints[0].CheckpointMac);
            checkpoints[1].SeqFrom.Should().Be(checkpoints[0].SeqTo + 1);
        }

        // Nothing new to seal: idempotent clean run.
        (await RunVerify()).Should().Be(0);
        await using (var db = NewContext())
            (await db.AuditCheckpoints.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Verify_EntryContentTamper_ExitsOne()
    {
        var ids = await SeedEntriesWithAudit(2);
        (await RunVerify()).Should().Be(0);

        // DB-only writer edits content without the key: the stored MAC no longer matches.
        await using (var db = NewContext())
        {
            var entry = await db.ExpertiseEntries.IgnoreQueryFilters().SingleAsync(e => e.Id == ids[0]);
            entry.Body = "tampered by a database-level writer";
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(1);
        await using (var verify = NewContext())
            (await verify.IntegrityVerificationStates.SingleAsync()).LastResult.Should().Be("mismatch");
    }

    [Fact]
    public async Task Verify_AuditRowEdit_InsideSealedRange_ExitsOne()
    {
        await SeedEntriesWithAudit(3);
        (await RunVerify()).Should().Be(0);

        await using (var db = NewContext())
        {
            var row = await db.ExpertiseAuditLogs.OrderBy(a => a.Seq).FirstAsync();
            row.Principal = "rewritten-attribution";
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(1);
    }

    [Fact]
    public async Task Verify_AuditRowDeletion_InsideSealedRange_ExitsOne()
    {
        await SeedEntriesWithAudit(3);
        (await RunVerify()).Should().Be(0);

        await using (var db = NewContext())
        {
            var row = await db.ExpertiseAuditLogs.OrderBy(a => a.Seq).FirstAsync();
            db.ExpertiseAuditLogs.Remove(row);
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(1);
    }

    [Fact]
    public async Task Verify_CheckpointRowTamper_ExitsOne()
    {
        await SeedEntriesWithAudit(2);
        (await RunVerify()).Should().Be(0);

        // Rewriting the sealed root to "legitimize" a tampered range fails without the
        // key: the checkpoint MAC no longer verifies.
        await using (var db = NewContext())
        {
            var checkpoint = await db.AuditCheckpoints.SingleAsync();
            checkpoint.MerkleRoot = new string('0', 64);
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(1);
    }

    [Fact]
    public async Task Verify_LegacyAndUnhashedEntries_AreReportedNonFatal()
    {
        // Soft-require states (ADR-020): a matching legacy bare-hex hash and a null
        // hash are counted, not failed.
        await using (var db = NewContext())
        {
            var legacy = TestHelpers.SeedEntry(domain: "iv", title: "legacy", body: "b");
            legacy.IntegrityHash = IntegrityHashService.Compute(legacy);
            var unhashed = TestHelpers.SeedEntry(domain: "iv", title: "unhashed", body: "b");
            unhashed.IntegrityHash = null;
            db.ExpertiseEntries.AddRange(legacy, unhashed);
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(0);

        await using var verifyDb = NewContext();
        var state = await verifyDb.IntegrityVerificationStates.SingleAsync();
        state.LastResult.Should().Be("clean");
        state.LegacyCount.Should().Be(1);
        state.UnhashedCount.Should().Be(1);
    }

    [Fact]
    public async Task Verify_UnknownKeyId_ExitsOne()
    {
        await using (var db = NewContext())
        {
            var entry = TestHelpers.SeedEntry(domain: "iv", title: "orphan-key", body: "b");
            entry.IntegrityHash = $"k9:{new string('a', 64)}";
            db.ExpertiseEntries.Add(entry);
            await db.SaveChangesAsync();
        }

        (await RunVerify()).Should().Be(1);
    }

    [Fact]
    public async Task Verify_NoSealAndGraceWindow_DoNotSeal()
    {
        // --no-seal never seals; a fresh row inside the grace window is not sealable
        // either (in-flight-transaction guard, ADR-020).
        await SeedEntriesWithAudit(1, timestampAge: TimeSpan.Zero);

        (await RunVerify("--no-seal")).Should().Be(0);
        (await RunVerifyArgs(["verify", "--grace-seconds", "300"])).Should().Be(0);

        await using var db = NewContext();
        (await db.AuditCheckpoints.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Backup_ExportsCheckpointChain_WithManifestRoot()
    {
        await SeedEntriesWithAudit(2);
        (await RunVerify()).Should().Be(0);

        var outputDir = Path.Join(Path.GetTempPath(), $"integrity-backup-{Guid.NewGuid():N}");
        try
        {
            await using (var app = BuildApp())
                (await BackupCommand.RunAsync(app, ["backup", "--output", outputDir])).Should().Be(0);

            var checkpointLines = await File.ReadAllLinesAsync(Path.Join(outputDir, "checkpoints.jsonl"));
            checkpointLines.Should().HaveCount(1);
            checkpointLines[0].Should().Contain("\"checkpointMac\":\"k1:");

            var manifest = await File.ReadAllTextAsync(Path.Join(outputDir, "manifest.json"));
            manifest.Should().Contain("\"checkpointCount\":1");
            manifest.Should().MatchRegex("\"checkpointsMerkleRoot\":\"[0-9a-f]{64}\"");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    // ---- Helpers ----------------------------------------------------------

    /// <summary>Seeds keyed entries each with one audit row aged out of the grace window.</summary>
    private async Task<List<Guid>> SeedEntriesWithAudit(int count, TimeSpan? timestampAge = null)
    {
        var ids = new List<Guid>();
        var timestamp = DateTime.UtcNow - (timestampAge ?? TimeSpan.FromMinutes(10));
        for (var i = 0; i < count; i++)
        {
            await using var db = NewContext();
            var entry = TestHelpers.SeedEntry(domain: "iv", title: $"entry {Guid.NewGuid():N}", body: $"body {i}");
            entry.IntegrityHash = IntegrityHashService.Compute(entry, Keyed.ActiveKey);
            db.ExpertiseEntries.Add(entry);
            await db.SaveChangesAsync(); // Id is DB-generated — the FK below needs it materialized
            db.ExpertiseAuditLogs.Add(new ExpertiseAuditLog
            {
                Timestamp = timestamp,
                Action = AuditAction.Created,
                EntryId = entry.Id,
                Tenant = entry.Tenant,
                Principal = "seed-principal",
                AfterHash = entry.IntegrityHash,
                ActorClass = ActorClass.Service,
            });
            await db.SaveChangesAsync();
            ids.Add(entry.Id);
        }

        return ids;
    }

    private async Task<int> RunVerify(params string[] extraArgs) =>
        await RunVerifyArgs(["verify", "--grace-seconds", "0", .. extraArgs]);

    private async Task<int> RunVerifyArgs(string[] args)
    {
        await using var app = BuildApp();
        return await VerifyCommand.RunAsync(app, args);
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
        builder.Services.AddSingleton<IIntegrityKeyProvider>(Keyed);
        builder.Services.AddSingleton<ITenantContextAccessor, NoOpTenantContextAccessor>();
        builder.Services.AddDbContext<ExpertiseDbContext>(o =>
            o.UseNpgsql(_container.GetConnectionString(), x => x.UseVector()));
        builder.Services.AddScoped<IntegrityVerificationService>();
        return builder.Build();
    }
}
