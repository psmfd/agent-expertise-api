using ExpertiseApi.Data;

namespace ExpertiseApi.Services;

/// <summary>
/// Reciprocal Rank Fusion over independently-ranked search result lists (ADR-016, #428).
/// Rank-based and scale-free: per-arm scores (unbounded <c>ts_rank_cd</c>, bounded cosine
/// similarity) are deliberately ignored — only positions matter, so no cross-mode score
/// normalization is needed. Pure in-process logic; never touches the database.
/// </summary>
internal static class RankFusion
{
    /// <summary>Conventional RRF constant — dampens the dominance of top ranks.</summary>
    internal const int K = 60;

    /// <summary>Candidate depth fetched from each arm before fusion.</summary>
    internal const int CandidatePoolSize = 50;

    /// <summary>
    /// Fuses two ranked lists with <c>score = Σ 1/(K + rank)</c> (1-based rank, summed
    /// across arms for entries present in both). Ties — common when two entries appear
    /// in only one arm each, at the same rank — break by <c>UpdatedAt</c> descending: a
    /// mild running-log freshness preference that never overrides a relevance
    /// difference (ADR-016). Returns at most <paramref name="limit"/> entries with the
    /// fused RRF sum as their score.
    /// </summary>
    public static List<ScoredEntry> ReciprocalRankFusion(
        IReadOnlyList<ScoredEntry> keywordRanked,
        IReadOnlyList<ScoredEntry> semanticRanked,
        int limit)
    {
        var fused = new Dictionary<Guid, (Models.ExpertiseEntry Entry, double Score)>();

        void Accumulate(IReadOnlyList<ScoredEntry> ranked)
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                var entry = ranked[i].Entry;
                var contribution = 1.0 / (K + i + 1);
                fused[entry.Id] = fused.TryGetValue(entry.Id, out var existing)
                    ? (existing.Entry, existing.Score + contribution)
                    : (entry, contribution);
            }
        }

        Accumulate(keywordRanked);
        Accumulate(semanticRanked);

        return fused.Values
            .OrderByDescending(f => f.Score)
            .ThenByDescending(f => f.Entry.UpdatedAt)
            .Take(limit)
            .Select(f => new ScoredEntry(f.Entry, f.Score))
            .ToList();
    }
}
