using System.Security.Cryptography;
using System.Text;
using ExpertiseApi.Cli;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace ExpertiseApi.Data;

/// <summary>Aggregate outcome of one verification run (ADR-020).</summary>
internal sealed record IntegrityVerificationResult
{
    public int EntriesChecked { get; init; }
    public int EntryMacMismatches { get; init; }
    public int UnknownKeyIds { get; init; }
    public int LegacyCount { get; init; }
    public int UnhashedCount { get; init; }
    public int CheckpointsChecked { get; init; }
    public int CheckpointMismatches { get; init; }
    public bool CheckpointSealed { get; init; }
    public long? SealedThroughSeq { get; init; }

    public int TotalMismatches => EntryMacMismatches + UnknownKeyIds + CheckpointMismatches;
    public bool Clean => TotalMismatches == 0;
}

/// <summary>
/// The ADR-020 verification sweep (decision 3A): recomputes every entry's
/// <c>IntegrityHash</c> per its format prefix, re-verifies every sealed audit
/// checkpoint (Merkle root, MAC, chain link), then seals a new checkpoint through the
/// latest grace-aged audit row. Invoked by the <c>verify</c> CLI verb; lives in
/// <c>Data/</c> because it needs raw <see cref="ExpertiseDbContext"/> access
/// (cross-tenant, cursor-paged sweeps the repository interface deliberately does not
/// expose — same rationale as the CLI commands). Extracted from the CLI so an
/// in-process sweep (ADR-020 option 3B) stays a registration away.
/// </summary>
internal sealed class IntegrityVerificationService(
    ExpertiseDbContext db,
    IIntegrityKeyProvider integrityKeys,
    ILogger<IntegrityVerificationService> logger)
{
    private static readonly Counter RunsCounter = Metrics.CreateCounter(
        "expertise_integrity_verify_runs_total",
        "Total integrity verification runs (ADR-020).",
        new CounterConfiguration { LabelNames = ["result"] });

    private static readonly Counter MismatchCounter = Metrics.CreateCounter(
        "expertise_integrity_mismatches_total",
        "Total integrity mismatches detected by verification runs (ADR-020).",
        new CounterConfiguration { LabelNames = ["kind"] });

    /// <summary>Default settle time before an audit row becomes sealable — long enough that no request-scoped audit-writing transaction can still be in flight.</summary>
    public static readonly TimeSpan DefaultGraceWindow = TimeSpan.FromSeconds(300);

    public async Task<IntegrityVerificationResult> RunAsync(
        int batchSize, TimeSpan graceWindow, bool seal, CancellationToken ct = default)
    {
        var (entriesChecked, entryMismatches, unknownKeys, legacy, unhashed) =
            await VerifyEntriesAsync(batchSize, ct);

        var (checkpointsChecked, checkpointMismatches) = await VerifyCheckpointsAsync(batchSize, ct);

        var sealedThrough = (long?)null;
        var sealedNow = false;
        if (seal && checkpointMismatches == 0)
        {
            sealedThrough = await SealCheckpointAsync(batchSize, graceWindow, ct);
            sealedNow = sealedThrough is not null;
        }
        else if (seal)
        {
            // Sealing on top of a tampered chain would launder the tampered state into
            // a fresh, validly-MACed link. Leave the chain as evidence.
            logger.LogWarning(
                "Verify: skipping checkpoint seal — {Mismatches} checkpoint mismatch(es) must be investigated first",
                checkpointMismatches);
        }

        var result = new IntegrityVerificationResult
        {
            EntriesChecked = entriesChecked,
            EntryMacMismatches = entryMismatches,
            UnknownKeyIds = unknownKeys,
            LegacyCount = legacy,
            UnhashedCount = unhashed,
            CheckpointsChecked = checkpointsChecked,
            CheckpointMismatches = checkpointMismatches,
            CheckpointSealed = sealedNow,
            SealedThroughSeq = sealedThrough,
        };

        await PersistStateAsync(result, ct);
        RunsCounter.WithLabels(result.Clean ? "clean" : "mismatch").Inc();
        return result;
    }

    private async Task<(int Checked, int Mismatches, int UnknownKeys, int Legacy, int Unhashed)>
        VerifyEntriesAsync(int batchSize, CancellationToken ct)
    {
        int checkedCount = 0, mismatches = 0, unknownKeys = 0, legacy = 0, unhashed = 0;
        Guid? lastId = null;

        while (true)
        {
            // Cross-tenant sweep — bypass the EF tenant query filter explicitly (same
            // rationale as ReembedCommand/RehashCommand).
            var query = db.ExpertiseEntries
                .IgnoreQueryFilters()
                .OrderBy(e => e.Id)
                .AsQueryable();

            if (lastId is not null)
                query = query.Where(e => e.Id > lastId.Value);

            var entries = await query.Take(batchSize).AsNoTracking().ToListAsync(ct);
            if (entries.Count == 0)
                break;

            foreach (var entry in entries)
            {
                checkedCount++;
                var stored = entry.IntegrityHash;

                if (stored is null)
                {
                    // Soft-require phase state — reported, non-fatal. Run `rehash`.
                    unhashed++;
                    continue;
                }

                var separator = stored.IndexOf(':', StringComparison.Ordinal);
                if (separator < 0)
                {
                    // Legacy unkeyed format. A non-matching legacy hash is still a
                    // detected inconsistency (content changed under the stored hash),
                    // even though a legacy hash is forgeable by design.
                    legacy++;
                    if (!HashesEqual(IntegrityHashService.Compute(entry), stored))
                    {
                        mismatches++;
                        MismatchCounter.WithLabels("entry_mac").Inc();
                        logger.LogError("Verify: entry {Id} legacy IntegrityHash does not match recomputed content hash", entry.Id);
                    }

                    continue;
                }

                var key = integrityKeys.ResolveKey(stored[..separator]);
                if (key is null)
                {
                    unknownKeys++;
                    MismatchCounter.WithLabels("entry_unknown_key").Inc();
                    logger.LogError(
                        "Verify: entry {Id} IntegrityHash uses unknown key id '{KeyId}' — add it to Integrity:RetiredKeys or investigate",
                        entry.Id, stored[..separator]);
                    continue;
                }

                if (!HashesEqual(IntegrityHashService.Compute(entry, key), stored))
                {
                    mismatches++;
                    MismatchCounter.WithLabels("entry_mac").Inc();
                    logger.LogError("Verify: entry {Id} IntegrityHash MAC mismatch — content was modified without the key", entry.Id);
                }
            }

            lastId = entries[^1].Id;
        }

        return (checkedCount, mismatches, unknownKeys, legacy, unhashed);
    }

    private async Task<(int Checked, int Mismatches)> VerifyCheckpointsAsync(int batchSize, CancellationToken ct)
    {
        var checkpoints = await db.AuditCheckpoints
            .AsNoTracking()
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var mismatches = 0;
        string? expectedPrevMac = null;

        foreach (var checkpoint in checkpoints)
        {
            // Chain link: each checkpoint must reference its predecessor's MAC; the
            // first link must reference nothing.
            if (!string.Equals(checkpoint.PrevCheckpointMac, expectedPrevMac, StringComparison.Ordinal))
            {
                mismatches++;
                MismatchCounter.WithLabels("checkpoint_chain").Inc();
                logger.LogError(
                    "Verify: checkpoint {Id} chain link broken — PrevCheckpointMac does not match the preceding checkpoint",
                    checkpoint.Id);
            }

            if (!VerifyCheckpointMac(checkpoint))
                mismatches++;

            // Recompute the Merkle root over the rows currently in the sealed range.
            var (root, rowCount) = await ComputeRangeRootAsync(checkpoint.SeqFrom, checkpoint.SeqTo, batchSize, ct);
            if (rowCount != checkpoint.RowCount || !string.Equals(root, checkpoint.MerkleRoot, StringComparison.Ordinal))
            {
                mismatches++;
                MismatchCounter.WithLabels("checkpoint_root").Inc();
                logger.LogError(
                    "Verify: checkpoint {Id} (seq {From}-{To}) Merkle root mismatch — sealed {SealedCount} rows, range now holds {LiveCount}; an audit row was edited, deleted, or inserted post-seal",
                    checkpoint.Id, checkpoint.SeqFrom, checkpoint.SeqTo, checkpoint.RowCount, rowCount);
            }

            expectedPrevMac = checkpoint.CheckpointMac;
        }

        return (checkpoints.Count, mismatches);
    }

    private bool VerifyCheckpointMac(AuditCheckpoint checkpoint)
    {
        IntegrityKey? key = null;
        var separator = checkpoint.CheckpointMac.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0)
        {
            key = integrityKeys.ResolveKey(checkpoint.CheckpointMac[..separator]);
            if (key is null)
            {
                MismatchCounter.WithLabels("checkpoint_mac").Inc();
                logger.LogError(
                    "Verify: checkpoint {Id} MAC uses unknown key id '{KeyId}'",
                    checkpoint.Id, checkpoint.CheckpointMac[..separator]);
                return false;
            }
        }

        var recomputed = CheckpointMacService.Compute(
            checkpoint.SeqFrom, checkpoint.SeqTo, checkpoint.RowCount, checkpoint.MerkleRoot,
            checkpoint.PrevCheckpointMac, checkpoint.CreatedAt, key);

        if (HashesEqual(recomputed, checkpoint.CheckpointMac))
            return true;

        MismatchCounter.WithLabels("checkpoint_mac").Inc();
        logger.LogError(
            "Verify: checkpoint {Id} MAC mismatch — the checkpoint row itself was modified",
            checkpoint.Id);
        return false;
    }

    private async Task<long?> SealCheckpointAsync(int batchSize, TimeSpan graceWindow, CancellationToken ct)
    {
        var lastCheckpoint = await db.AuditCheckpoints
            .AsNoTracking()
            .OrderByDescending(c => c.Id)
            .FirstOrDefaultAsync(ct);

        var sealedThrough = lastCheckpoint?.SeqTo ?? 0L;

        // The grace window keeps in-flight audit-writing transactions out of the
        // sealed range: a sequence value is assigned at INSERT time, before COMMIT, so
        // sealing right up to max(Seq) could exclude a row that later commits inside
        // the range and read as tampering. Audit writes are request-scoped
        // (milliseconds); minutes of settle time make that race implausible.
        var sealBefore = DateTime.UtcNow - graceWindow;

        var boundary = await db.ExpertiseAuditLogs
            .AsNoTracking()
            .Where(a => a.Seq > sealedThrough && a.Timestamp < sealBefore)
            .Select(a => (long?)a.Seq)
            .MaxAsync(ct);

        if (boundary is null)
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Verify: no grace-aged audit rows beyond seq {SealedThrough} — nothing to seal", sealedThrough);
            return null;
        }

        var (root, rowCount) = await ComputeRangeRootAsync(sealedThrough + 1, boundary.Value, batchSize, ct);

        var createdAt = DateTime.UtcNow;
        var checkpoint = new AuditCheckpoint
        {
            SeqFrom = sealedThrough + 1,
            SeqTo = boundary.Value,
            RowCount = rowCount,
            MerkleRoot = root,
            PrevCheckpointMac = lastCheckpoint?.CheckpointMac,
            CheckpointMac = CheckpointMacService.Compute(
                sealedThrough + 1, boundary.Value, rowCount, root,
                lastCheckpoint?.CheckpointMac, createdAt, integrityKeys.ActiveKey),
            CreatedAt = createdAt,
        };

        db.AuditCheckpoints.Add(checkpoint);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Verify: sealed checkpoint over seq {From}-{To} ({RowCount} rows, keyed={Keyed})",
                checkpoint.SeqFrom, checkpoint.SeqTo, rowCount, integrityKeys.ActiveKey is not null);
        }

        return boundary;
    }

    private async Task<(string Root, int RowCount)> ComputeRangeRootAsync(
        long seqFrom, long seqTo, int batchSize, CancellationToken ct)
    {
        var leaves = new List<string>();
        var lastSeq = seqFrom - 1;

        while (true)
        {
            var rows = await db.ExpertiseAuditLogs
                .AsNoTracking()
                .Where(a => a.Seq > lastSeq && a.Seq <= seqTo)
                .OrderBy(a => a.Seq)
                .Take(batchSize)
                .ToListAsync(ct);

            if (rows.Count == 0)
                break;

            foreach (var row in rows)
                leaves.Add(BackupRecordHash.ComputeAudit(ToBackupRecord(row)));

            lastSeq = rows[^1].Seq;
        }

        return (MerkleTree.ComputeRoot(leaves), leaves.Count);
    }

    /// <summary>
    /// Checkpoint Merkle leaves reuse <see cref="BackupRecordHash.ComputeAudit"/> — one
    /// canonicalization for audit rows everywhere (backup and chain agree on what a
    /// row's hash is). <c>Seq</c> is deliberately not part of the canonical form (it is
    /// a server-generated ordinal, regenerated on restore); range membership and leaf
    /// ORDER are still bound by Seq, so swapping two rows' Seq values inside a sealed
    /// range reorders the leaves and diverges the root.
    /// </summary>
    private static BackupAuditRecord ToBackupRecord(ExpertiseAuditLog row) => new()
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

    private async Task PersistStateAsync(IntegrityVerificationResult result, CancellationToken ct)
    {
        var state = await db.IntegrityVerificationStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new IntegrityVerificationState { LastResult = "" };
            db.IntegrityVerificationStates.Add(state);
        }

        state.LastRunAt = DateTime.UtcNow;
        state.LastResult = result.Clean ? "clean" : "mismatch";
        state.MismatchCount = result.TotalMismatches;
        state.LegacyCount = result.LegacyCount;
        state.UnhashedCount = result.UnhashedCount;
        await db.SaveChangesAsync(ct);
    }

    private static bool HashesEqual(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
