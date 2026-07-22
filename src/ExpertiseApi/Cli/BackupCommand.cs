using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using ExpertiseApi.Data;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Cli;

/// <summary>
/// One-shot CLI verb that exports every expertise entry (all tenants, all review
/// states) and the full audit log as NDJSON plus a manifest carrying RFC 6962
/// Merkle roots (ADR-012). Emits three plain files into <c>--output</c>:
/// <c>entries.jsonl</c>, <c>audit.jsonl</c>, <c>manifest.json</c>. Compression,
/// age encryption, and cosign signing are the orchestration wrapper's job
/// (<c>scripts/expertise-apictl backup</c>) — the binary alone produces a valid
/// unsigned payload for dev loops.
///
/// Idempotency-store rows are deliberately excluded (24h-TTL ephemera, ADR-010).
/// The export pages keyset-style by Id inside ONE RepeatableRead transaction so a
/// multi-page export is a consistent PostgreSQL snapshot.
/// </summary>
internal static class BackupCommand
{
    public static bool IsBackupRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("backup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exports all entries and audit rows as NDJSON plus an RFC 6962 Merkle manifest (ADR-012).</summary>
    /// <returns>0 on success; 1 on any database or filesystem failure.</returns>
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Backup");

        var outputDir = GetOption(args, "--output") ?? Directory.GetCurrentDirectory();
        var instanceId = GetOption(args, "--instance-id") ?? Environment.MachineName;
        var batchSize = GetBatchSize(args);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();

        try
        {
            Directory.CreateDirectory(outputDir);
            var entriesPath = Path.Join(outputDir, "entries.jsonl");
            var auditPath = Path.Join(outputDir, "audit.jsonl");
            var manifestPath = Path.Join(outputDir, "manifest.json");

            foreach (var path in new[] { entriesPath, auditPath, manifestPath })
            {
                if (File.Exists(path))
                {
                    logger.LogCritical("Backup: refusing to overwrite existing file {Path}. Use an empty output directory.", path);
                    return 1;
                }
            }

            // One snapshot for the whole export: RepeatableRead on PostgreSQL gives
            // snapshot isolation, so every page below sees the same committed state
            // even while the API keeps writing.
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);

            var dbSchemaVersion = (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault();
            var embeddingMetadata = await db.EmbeddingMetadata.FirstOrDefaultAsync();

            var entryHashes = new List<string>();
            await using (var writer = new StreamWriter(File.Create(entriesPath), new UTF8Encoding(false)))
            {
                Guid? lastId = null;
                while (true)
                {
                    // CLI must export every tenant — bypass the EF tenant query filter
                    // explicitly (same rationale as ReembedCommand/RehashCommand).
                    var query = db.ExpertiseEntries
                        .IgnoreQueryFilters()
                        .OrderBy(e => e.Id)
                        .AsQueryable();

                    if (lastId is not null)
                        query = query.Where(e => e.Id > lastId.Value);

                    var entries = await query.Take(batchSize).AsNoTracking().ToListAsync();
                    if (entries.Count == 0)
                        break;

                    foreach (var entry in entries)
                    {
                        var record = new BackupEntryRecord
                        {
                            Id = entry.Id,
                            Domain = entry.Domain,
                            Tags = [.. entry.Tags],
                            Title = entry.Title,
                            Body = entry.Body,
                            EntryType = entry.EntryType.ToString(),
                            Severity = entry.Severity.ToString(),
                            Source = entry.Source,
                            SourceVersion = entry.SourceVersion,
                            Embedding = entry.Embedding?.ToArray(),
                            CreatedAt = entry.CreatedAt,
                            UpdatedAt = entry.UpdatedAt,
                            DeprecatedAt = entry.DeprecatedAt,
                            Tenant = entry.Tenant,
                            Visibility = entry.Visibility.ToString(),
                            AuthorPrincipal = entry.AuthorPrincipal,
                            AuthorAgent = entry.AuthorAgent,
                            IntegrityHash = entry.IntegrityHash,
                            ReviewState = entry.ReviewState.ToString(),
                            ReviewedBy = entry.ReviewedBy,
                            ReviewedAt = entry.ReviewedAt,
                            RejectionReason = entry.RejectionReason,
                            RecordHash = "",
                        };
                        record = record with { RecordHash = BackupRecordHash.ComputeEntry(record) };

                        entryHashes.Add(record.RecordHash);
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(record, BackupJsonContext.Default.BackupEntryRecord));
                    }

                    lastId = entries[^1].Id;
                    logger.LogInformation("Backup: exported {Count} entries so far", entryHashes.Count);
                }
            }

            var auditHashes = new List<string>();
            await using (var writer = new StreamWriter(File.Create(auditPath), new UTF8Encoding(false)))
            {
                Guid? lastId = null;
                while (true)
                {
                    var query = db.ExpertiseAuditLogs
                        .OrderBy(a => a.Id)
                        .AsQueryable();

                    if (lastId is not null)
                        query = query.Where(a => a.Id > lastId.Value);

                    var rows = await query.Take(batchSize).AsNoTracking().ToListAsync();
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        var record = new BackupAuditRecord
                        {
                            Id = row.Id,
                            Timestamp = row.Timestamp,
                            Action = row.Action.ToString(),
                            EntryId = row.EntryId,
                            Tenant = row.Tenant,
                            Principal = row.Principal,
                            Agent = row.Agent,
                            BeforeHash = row.BeforeHash,
                            AfterHash = row.AfterHash,
                            IpAddress = row.IpAddress,
                            ActorClass = row.ActorClass.ToString(),
                            AuthMethod = row.AuthMethod,
                            ActorClassHeader = row.ActorClassHeader,
                            RecordHash = "",
                        };
                        record = record with { RecordHash = BackupRecordHash.ComputeAudit(record) };

                        auditHashes.Add(record.RecordHash);
                        await writer.WriteLineAsync(
                            JsonSerializer.Serialize(record, BackupJsonContext.Default.BackupAuditRecord));
                    }

                    lastId = rows[^1].Id;
                }
            }

            var manifest = new BackupManifest
            {
                SchemaVersion = 1,
                InstanceId = instanceId,
                ExportedAt = DateTime.UtcNow,
                EntryCount = entryHashes.Count,
                AuditCount = auditHashes.Count,
                EntriesMerkleRoot = MerkleTree.ComputeRoot(entryHashes),
                AuditMerkleRoot = MerkleTree.ComputeRoot(auditHashes),
                DbSchemaVersion = dbSchemaVersion,
                EmbeddingModel = embeddingMetadata is null
                    ? null
                    : new BackupEmbeddingModel { Name = embeddingMetadata.ModelName, Dims = embeddingMetadata.Dimensions },
                PayloadSha256 = null,
            };

            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest),
                new UTF8Encoding(false));

            logger.LogInformation(
                "Backup: complete — {EntryCount} entries, {AuditCount} audit rows, schema {Schema}, output {Output}",
                manifest.EntryCount, manifest.AuditCount, dbSchemaVersion ?? "(none)", outputDir);
            return 0;
        }
        // Same narrowed-catch posture as MigrateCommand (process-fatal exceptions and
        // OperationCanceledException propagate); IOException/UnauthorizedAccessException
        // added because this verb writes to the filesystem.
        catch (Exception ex) when (ex is DbException
                                      or DbUpdateException
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            logger.LogCritical(ex, "Backup: failed (full exception detail follows).");
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    private static int GetBatchSize(string[] args)
    {
        var idx = Array.IndexOf(args, "--batch-size");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var size) && size > 0)
            return size;
        return 500;
    }
}
