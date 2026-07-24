using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Endpoints;
using ExpertiseApi.Models;
using Microsoft.Extensions.Options;
using Pgvector;

namespace ExpertiseApi.Services;

internal class DeduplicationOptions
{
    public bool Enabled { get; set; } = true;

    // Cosine-DISTANCE ceiling for the semantic near-duplicate check, calibrated
    // per embedding model (#457, ADR-017 Amendment 1): under jina-v2-small the
    // live corpus's true near-dups sit at <= 0.048 and its closest genuinely
    // distinct same-domain neighbors begin at ~0.051, so 0.05 splits the valley.
    // Biased LOW by design — a false 409 rejects a legitimate write, a miss
    // merely lands as a Draft for curator review. Gate any change with
    // EXPERTISE_EVAL=1 DedupThresholdEvalTests.
    public double SemanticThreshold { get; set; } = 0.05;
}

internal class DeduplicationService(IExpertiseRepository repo, IOptions<DeduplicationOptions> options)
{
    public async Task<(bool IsDuplicate, ExpertiseEntry? Existing)> CheckAsync(
        CreateExpertiseRequest request, Vector embedding, TenantContext ctx, CancellationToken ct = default)
    {
        var opts = options.Value;

        if (!opts.Enabled)
            return (false, null);

        // Tenant-scoped dedup: an entry in tenant A must NEVER be reported as a duplicate
        // of a tenant B entry — that would leak the B entry's full content via the 409
        // Conflict response body. The repository already filters by ctx.Tenant.
        var exact = await repo.FindExactMatchAsync(request.Domain, request.Title, ctx, ct);
        if (exact is not null && exact.Body == request.Body)
            return (true, exact);

        var nearest = await repo.FindNearestInDomainAsync(request.Domain, embedding, opts.SemanticThreshold, ctx, ct);
        if (nearest is not null)
            return (true, nearest);

        return (false, null);
    }

    public async Task<IReadOnlyList<(bool IsDuplicate, ExpertiseEntry? Existing)>> CheckBatchAsync(
        IReadOnlyList<CreateExpertiseRequest> requests,
        IReadOnlyList<Vector> embeddings,
        TenantContext ctx,
        CancellationToken ct = default)
    {
        if (embeddings.Count != requests.Count)
            throw new ArgumentException(
                $"Embeddings count ({embeddings.Count}) does not match requests count ({requests.Count}).",
                nameof(embeddings));

        var opts = options.Value;
        var results = new (bool IsDuplicate, ExpertiseEntry? Existing)[requests.Count];

        if (!opts.Enabled)
            return results;

        // Group by domain for bulk queries
        var domainGroups = requests
            .Select((r, i) => (Request: r, Index: i, Embedding: embeddings[i]))
            .GroupBy(x => x.Request.Domain);

        foreach (var group in domainGroups)
        {
            var domain = group.Key;
            var items = group.ToList();

            // Bulk exact-match: one query per domain instead of N
            var titles = items.Select(x => x.Request.Title).ToList();
            var exactMatches = await repo.FindExactMatchesAsync(domain, titles, ctx, ct);

            // Map title -> list of candidates (OrdinalIgnoreCase) to handle multiple entries sharing the same title
            var matchesByTitle = exactMatches
                .GroupBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                // Check exact match — any candidate sharing the title whose body also matches
                if (matchesByTitle.TryGetValue(item.Request.Title, out var candidates))
                {
                    var bodyMatch = candidates.FirstOrDefault(c => c.Body == item.Request.Body);
                    if (bodyMatch is not null)
                    {
                        results[item.Index] = (true, bodyMatch);
                        continue;
                    }
                }

                // Semantic match via the same HNSW-indexed, DB-side nearest-neighbour query
                // the single-create path uses (#333 Finding 4). Replaces the former
                // "load every domain embedding into memory and brute-force in C#" scan,
                // which was O(batchSize x domainSize) and unbounded in memory as a domain
                // grew. Bounded to one indexed query per item; sequential because the
                // scoped DbContext is not thread-safe. Consolidating onto
                // FindNearestInDomainAsync also removes the behaviour drift between the
                // batch and single-create dedup paths. Trade: batch dedup is now
                // approximate-NN (HNSW) like single-create already is — a miss lands as a
                // Draft for curator review, per the DeduplicationOptions.SemanticThreshold
                // risk posture.
                var nearest = await repo.FindNearestInDomainAsync(
                    domain, item.Embedding, opts.SemanticThreshold, ctx, ct);

                results[item.Index] = nearest is not null ? (true, nearest) : (false, null);
            }
        }

        return results;
    }
}
