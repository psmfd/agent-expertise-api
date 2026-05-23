using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Microsoft.EntityFrameworkCore;

namespace ExpertiseApi.Cli;

internal static class ReembedCommand
{
    public static bool IsReembedRequested(string[] args) =>
        args.Length > 0 && args[0].Equals("reembed", StringComparison.OrdinalIgnoreCase);

    public static async Task RunAsync(WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Reembed");

        var batchSize = GetBatchSize(args);
        var processed = 0;
        Guid? lastId = null;

        logger.LogInformation("Starting reembed with batch size {BatchSize}", batchSize);

        while (true)
        {
            // CLI must process every tenant — bypass the EF tenant query filter explicitly.
            // The accessor returns null in CLI scope so the filter would short-circuit anyway,
            // but the explicit call documents intent and is robust against future changes to
            // the null-handling semantics in HasQueryFilter.
            var query = db.ExpertiseEntries.IgnoreQueryFilters().OrderBy(e => e.Id).AsQueryable();
            if (lastId is not null)
                query = query.Where(e => e.Id > lastId.Value);

            var entries = await query.Take(batchSize).ToListAsync();
            if (entries.Count == 0)
                break;

            foreach (var entry in entries)
            {
                entry.Embedding = await embeddingService.GenerateEmbeddingAsync(
                    EmbeddingService.BuildInputText(entry.Title, entry.Body));
            }

            await db.SaveChangesAsync();
            lastId = entries[^1].Id;
            processed += entries.Count;
            logger.LogInformation("Reembedded {Processed} entries", processed);
        }

        var metadata = await db.EmbeddingMetadata.FirstOrDefaultAsync()
            ?? db.EmbeddingMetadata.Add(new EmbeddingMetadata
            {
                ModelName = "bge-micro-v2",
                Dimensions = 384
            }).Entity;

        metadata.LastReembedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Reembed complete — {Processed} entries processed", processed);
    }

    private static int GetBatchSize(string[] args)
    {
        var idx = Array.IndexOf(args, "--batch-size");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var size))
            return size;
        return 50;
    }
}
