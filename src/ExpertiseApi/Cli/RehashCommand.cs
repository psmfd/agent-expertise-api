using ExpertiseApi.Data;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Cli;

internal static class RehashCommand
{
    public static bool IsRehashRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("rehash", StringComparison.OrdinalIgnoreCase);

    public static async Task RunAsync(WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Rehash");
        var integrityKeys = scope.ServiceProvider.GetRequiredService<IIntegrityKeyProvider>();

        var batchSize = GetBatchSize(args);
        // --force: recompute EVERY row, not just nulls — the one-time rekey migration
        // after configuring (or rotating) Integrity:HmacKey (ADR-020). Without it the
        // first `verify` run after a key change reports a false mass mismatch.
        var force = Array.Exists(args, a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        var processed = 0;
        Guid? lastId = null;

        logger.LogInformation(
            "Starting rehash with batch size {BatchSize} (force={Force}, keyed={Keyed})",
            batchSize, force, integrityKeys.ActiveKey is not null);

        while (true)
        {
            // CLI must process every tenant — bypass the EF tenant query filter explicitly.
            // See ReembedCommand for rationale.
            var query = db.ExpertiseEntries
                .IgnoreQueryFilters()
                .AsQueryable();

            if (!force)
                query = query.Where(e => e.IntegrityHash == null);

            query = query.OrderBy(e => e.Id);

            if (lastId is not null)
                query = query.Where(e => e.Id > lastId.Value);

            var entries = await query.Take(batchSize).ToListAsync();
            if (entries.Count == 0)
                break;

            foreach (var entry in entries)
            {
                entry.IntegrityHash = IntegrityHashService.Compute(entry, integrityKeys.ActiveKey);
            }

            await db.SaveChangesAsync();
            // Same tracker-bounding as ReembedCommand (review finding, 2026-07-23).
            db.ChangeTracker.Clear();
            lastId = entries[^1].Id;
            processed += entries.Count;
            logger.LogInformation("Rehashed {Processed} entries", processed);
        }

        logger.LogInformation("Rehash complete — {Processed} entries processed", processed);
    }

    private static int GetBatchSize(string[] args)
    {
        var idx = Array.IndexOf(args, "--batch-size");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var size))
            return size;
        return 50;
    }
}
