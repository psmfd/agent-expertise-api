using System.Data.Common;
using System.Text.Json;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace ExpertiseApi.Cli;

/// <summary>
/// One-shot CLI verb that imports a backup payload produced by <see cref="BackupCommand"/>
/// (ADR-012). Expects a directory containing the DECRYPTED plain files
/// <c>entries.jsonl</c>, <c>audit.jsonl</c>, <c>manifest.json</c> — signature
/// verification, decryption, and extraction are the wrapper's job
/// (<c>scripts/expertise-apictl restore</c>); this verb owns hash/Merkle verification
/// and the actual import.
///
/// Trust policy (ADR-012):
///   * ALL validation happens before ANY row is written.
///   * Merkle-root or count mismatch against the manifest → abort (fail closed).
///   * A single record whose recomputed hash differs from its stored hash →
///     imported but quarantined as Draft + a RestoreQuarantined audit row.
///   * <c>--force-draft</c> (foreign-backup seed) → every entry lands as Draft.
///   * v1 is <c>--mode replace</c> only: target tables must be empty.
///   * Pending EF migrations → abort (MigrateCommand idiom).
///   * Manifest embedding model vs live EmbeddingMetadata mismatch → abort loudly,
///     directing to <c>reembed</c>. Embeddings whose dimensions don't fit the
///     vector(384) column are skipped (regenerable), never a reason to abort.
/// </summary>
internal static class RestoreCommand
{
    public static bool IsRestoreRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("restore", StringComparison.OrdinalIgnoreCase);

    private const int EmbeddingDimensions = 384;
    private const string QuarantinePrincipal = "restore-cli";

    /// <returns>0 on success; 1 on validation or import failure.</returns>
    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Restore");

        var inputDir = GetOption(args, "--input");
        var mode = GetOption(args, "--mode") ?? "replace";
        var forceDraft = Array.IndexOf(args, "--force-draft") >= 0;
        var batchSize = GetBatchSize(args);

        if (inputDir is null)
        {
            logger.LogCritical("Restore: --input <dir> is required (directory holding entries.jsonl, audit.jsonl, manifest.json).");
            return 1;
        }

        if (!mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogCritical("Restore: only --mode replace is supported in v1 (merge mode is tracked in issue #343).");
            return 1;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();

        try
        {
            var manifestPath = Path.Combine(inputDir, "manifest.json");
            var entriesPath = Path.Combine(inputDir, "entries.jsonl");
            var auditPath = Path.Combine(inputDir, "audit.jsonl");

            foreach (var path in new[] { manifestPath, entriesPath, auditPath })
            {
                if (!File.Exists(path))
                {
                    logger.LogCritical("Restore: required file {Path} not found.", path);
                    return 1;
                }
            }

            var manifest = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(manifestPath), BackupJsonContext.Default.BackupManifest);
            if (manifest is null || manifest.SchemaVersion != 1)
            {
                logger.LogCritical("Restore: unsupported or unreadable manifest (schemaVersion {Version}).",
                    manifest?.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(null)");
                return 1;
            }

            // Precondition: schema is current (same idiom as MigrateCommand).
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0)
            {
                logger.LogCritical(
                    "Restore: {Count} pending migration(s) ({Migrations}) — run `migrate` first, then retry.",
                    pending.Count, string.Join(", ", pending));
                return 1;
            }

            // Precondition: replace mode requires an empty target.
            if (await db.ExpertiseEntries.IgnoreQueryFilters().AnyAsync()
                || await db.ExpertiseAuditLogs.AnyAsync())
            {
                logger.LogCritical("Restore: target database is not empty — --mode replace requires empty ExpertiseEntries and ExpertiseAuditLogs tables.");
                return 1;
            }

            // Embedding-model compatibility (fail loud, ADR-012). On a fresh empty
            // target the EmbeddingMetadata row normally doesn't exist yet; when it
            // does, it must agree with the manifest.
            var liveModel = await db.EmbeddingMetadata.FirstOrDefaultAsync();
            if (liveModel is not null && manifest.EmbeddingModel is not null
                && (!string.Equals(liveModel.ModelName, manifest.EmbeddingModel.Name, StringComparison.Ordinal)
                    || liveModel.Dimensions != manifest.EmbeddingModel.Dims))
            {
                logger.LogCritical(
                    "Restore: embedding model mismatch — manifest declares {ManifestModel}/{ManifestDims}, live EmbeddingMetadata is {LiveModel}/{LiveDims}. Restore without embeddings is not automatic: resolve the mismatch, or reembed after restore.",
                    manifest.EmbeddingModel.Name, manifest.EmbeddingModel.Dims, liveModel.ModelName, liveModel.Dimensions);
                return 1;
            }

            var importEmbeddings = manifest.EmbeddingModel is { Dims: EmbeddingDimensions };
            if (!importEmbeddings && manifest.EmbeddingModel is not null)
            {
                logger.LogWarning(
                    "Restore: manifest embedding model {Model}/{Dims} does not fit the vector({Expected}) column — embeddings will be skipped; run `reembed` after restore.",
                    manifest.EmbeddingModel.Name, manifest.EmbeddingModel.Dims, EmbeddingDimensions);
            }

            // ---- Phase 1: parse + verify everything BEFORE writing anything. ----

            var entryRecords = new List<BackupEntryRecord>();
            var entryHashes = new List<string>();
            var quarantined = new HashSet<Guid>();

            await foreach (var (record, lineNumber) in ReadNdjsonAsync(entriesPath, BackupJsonContext.Default.BackupEntryRecord))
            {
                var recomputed = BackupRecordHash.ComputeEntry(record);
                if (!string.Equals(recomputed, record.RecordHash, StringComparison.Ordinal))
                {
                    quarantined.Add(record.Id);
                    logger.LogWarning(
                        "Restore: record hash mismatch for entry {Id} (line {Line}) — will quarantine as Draft.",
                        record.Id, lineNumber);
                }

                entryHashes.Add(record.RecordHash);
                entryRecords.Add(record);
            }

            var auditRecords = new List<BackupAuditRecord>();
            var auditHashes = new List<string>();
            var auditTampered = false;

            await foreach (var (record, lineNumber) in ReadNdjsonAsync(auditPath, BackupJsonContext.Default.BackupAuditRecord))
            {
                var recomputed = BackupRecordHash.ComputeAudit(record);
                if (!string.Equals(recomputed, record.RecordHash, StringComparison.Ordinal))
                {
                    // Unlike entries, audit history has no meaningful "quarantine" state —
                    // a tampered audit row poisons the trail it exists to protect.
                    auditTampered = true;
                    logger.LogCritical("Restore: audit record hash mismatch for row {Id} (line {Line}).", record.Id, lineNumber);
                }

                auditHashes.Add(record.RecordHash);
                auditRecords.Add(record);
            }

            if (auditTampered)
            {
                logger.LogCritical("Restore: aborting — audit log integrity is all-or-nothing.");
                return 1;
            }

            if (entryRecords.Count != manifest.EntryCount || auditRecords.Count != manifest.AuditCount)
            {
                logger.LogCritical(
                    "Restore: count mismatch — manifest declares {MEntries}/{MAudit}, payload contains {PEntries}/{PAudit}.",
                    manifest.EntryCount, manifest.AuditCount, entryRecords.Count, auditRecords.Count);
                return 1;
            }

            if (!string.Equals(MerkleTree.ComputeRoot(entryHashes), manifest.EntriesMerkleRoot, StringComparison.Ordinal)
                || !string.Equals(MerkleTree.ComputeRoot(auditHashes), manifest.AuditMerkleRoot, StringComparison.Ordinal))
            {
                logger.LogCritical("Restore: Merkle root mismatch against manifest — payload record set does not match what was signed. Aborting (fail closed).");
                return 1;
            }

            // ---- Phase 2: import (entries first — audit rows FK them, ON DELETE RESTRICT). ----

            var imported = 0;
            foreach (var chunk in entryRecords.Chunk(batchSize))
            {
                foreach (var record in chunk)
                {
                    var toDraft = forceDraft || quarantined.Contains(record.Id);
                    var entity = new ExpertiseEntry
                    {
                        // Explicit values win over gen_random_uuid()/now() column
                        // defaults — the load-bearing invariant for Id/CreatedAt/UpdatedAt
                        // preservation (integration-tested).
                        Id = record.Id,
                        Domain = record.Domain,
                        Tags = [.. record.Tags],
                        Title = record.Title,
                        Body = record.Body,
                        EntryType = ParseEnum<EntryType>(record.EntryType, record.Id),
                        Severity = ParseEnum<Severity>(record.Severity, record.Id),
                        Source = record.Source,
                        SourceVersion = record.SourceVersion,
                        Embedding = importEmbeddings && record.Embedding is { Count: EmbeddingDimensions }
                            ? new Vector(record.Embedding.ToArray())
                            : null,
                        CreatedAt = record.CreatedAt,
                        UpdatedAt = record.UpdatedAt,
                        DeprecatedAt = record.DeprecatedAt,
                        Tenant = record.Tenant,
                        Visibility = ParseEnum<Visibility>(record.Visibility, record.Id),
                        AuthorPrincipal = record.AuthorPrincipal,
                        AuthorAgent = record.AuthorAgent,
                        IntegrityHash = record.IntegrityHash,
                        ReviewState = toDraft ? ReviewState.Draft : ParseEnum<ReviewState>(record.ReviewState, record.Id),
                        ReviewedBy = toDraft ? null : record.ReviewedBy,
                        ReviewedAt = toDraft ? null : record.ReviewedAt,
                        RejectionReason = toDraft ? null : record.RejectionReason,
                    };
                    db.ExpertiseEntries.Add(entity);
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                imported += chunk.Length;
                logger.LogInformation("Restore: imported {Imported}/{Total} entries", imported, entryRecords.Count);
            }

            foreach (var chunk in auditRecords.Chunk(batchSize))
            {
                foreach (var record in chunk)
                {
                    db.ExpertiseAuditLogs.Add(new ExpertiseAuditLog
                    {
                        Id = record.Id,
                        Timestamp = record.Timestamp,
                        Action = ParseEnum<AuditAction>(record.Action, record.Id),
                        EntryId = record.EntryId,
                        Tenant = record.Tenant,
                        Principal = record.Principal,
                        Agent = record.Agent,
                        BeforeHash = record.BeforeHash,
                        AfterHash = record.AfterHash,
                        IpAddress = record.IpAddress,
                        ActorClass = ParseEnum<ActorClass>(record.ActorClass, record.Id),
                        AuthMethod = record.AuthMethod,
                        ActorClassHeader = record.ActorClassHeader,
                    });
                }

                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }

            // New audit rows for quarantined entries. Before/AfterHash carry the
            // artifact's claimed BackupRecordHash vs the recomputed one — a deliberate
            // semantic difference from mutation rows (which carry IntegrityHash pairs):
            // it records WHAT disagreed, which is the forensically useful fact.
            foreach (var record in entryRecords.Where(r => quarantined.Contains(r.Id)))
            {
                db.ExpertiseAuditLogs.Add(new ExpertiseAuditLog
                {
                    Action = AuditAction.RestoreQuarantined,
                    EntryId = record.Id,
                    Tenant = record.Tenant,
                    Principal = QuarantinePrincipal,
                    ActorClass = ActorClass.Service,
                    BeforeHash = record.RecordHash,
                    AfterHash = BackupRecordHash.ComputeEntry(record),
                });
            }

            if (quarantined.Count > 0)
                await db.SaveChangesAsync();

            // Record the vintage of the imported vectors so the runbook's post-restore
            // check (and a future automated check, issue #345) has ground truth.
            // importEmbeddings (set above) is true only when manifest.EmbeddingModel
            // is non-null, so the null-forgiving access is safe; the extra
            // `manifest.EmbeddingModel is not null` here was redundant (CodeQL
            // cs/constant-condition — always true given importEmbeddings).
            if (importEmbeddings && liveModel is null)
            {
                var embeddingModel = manifest.EmbeddingModel!;
                db.EmbeddingMetadata.Add(new EmbeddingMetadata
                {
                    ModelName = embeddingModel.Name,
                    Dimensions = embeddingModel.Dims,
                    LastReembedAt = manifest.ExportedAt,
                });
                await db.SaveChangesAsync();
            }

            logger.LogInformation(
                "Restore: complete — {Entries} entries ({Quarantined} quarantined as Draft{ForceDraft}), {Audit} audit rows, embeddings {Embeddings}.",
                entryRecords.Count,
                quarantined.Count,
                forceDraft ? "; --force-draft: ALL entries landed as Draft" : "",
                auditRecords.Count,
                importEmbeddings ? "imported" : "skipped (run `reembed`)");
            return 0;
        }
        catch (Exception ex) when (ex is DbException
                                      or DbUpdateException
                                      or InvalidOperationException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or FormatException)
        {
            // Import commits per batch: a failure here can leave a partial import.
            // Replace mode makes remediation simple — wipe the target and retry.
            logger.LogCritical(ex, "Restore: failed (full exception detail follows). If rows were already imported, wipe the target and retry.");
            return 1;
        }
    }

    private static async IAsyncEnumerable<(T Record, int LineNumber)> ReadNdjsonAsync<T>(
        string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = JsonSerializer.Deserialize(line, typeInfo)
                ?? throw new JsonException($"null record at {Path.GetFileName(path)}:{lineNumber}");
            yield return (record, lineNumber);
        }
    }

    private static TEnum ParseEnum<TEnum>(string value, Guid recordId) where TEnum : struct, Enum
    {
        // Strict: exact member names only. TryParse alone would accept raw integers,
        // which a hand-edited artifact could smuggle past HasConversion<string>.
        if (Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            && Enum.IsDefined(parsed)
            && !char.IsAsciiDigit(value[0]))
        {
            return parsed;
        }

        throw new FormatException($"record {recordId}: '{value}' is not a valid {typeof(TEnum).Name}");
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
