using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;

namespace ExpertiseApi.Tests.Unit;

public class RankFusionTests
{
    private static ScoredEntry Hit(ExpertiseEntry entry, double score = 1.0) => new(entry, score);

    private static ExpertiseEntry Entry(string title, DateTime? updatedAt = null)
    {
        var entry = TestHelpers.SeedEntry(title: title);
        entry.Id = Guid.NewGuid();
        entry.UpdatedAt = updatedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return entry;
    }

    [Fact]
    public void EntryInBothArms_OutranksSingleArmEntries()
    {
        var both = Entry("in both arms");
        var keywordOnly = Entry("keyword only");
        var semanticOnly = Entry("semantic only");

        var fused = RankFusion.ReciprocalRankFusion(
            keywordRanked: [Hit(keywordOnly), Hit(both)],
            semanticRanked: [Hit(semanticOnly), Hit(both)],
            limit: 10);

        fused[0].Entry.Id.Should().Be(both.Id,
            "rank-2 in both arms (2/(K+2)) beats rank-1 in one arm (1/(K+1)) at K=60");
        fused[0].Score.Should().BeApproximately(2.0 / (RankFusion.K + 2), 1e-9);
        fused.Select(f => f.Entry.Id).Should().Contain([keywordOnly.Id, semanticOnly.Id],
            "single-arm hits still surface — union semantics, not intersection");
    }

    [Fact]
    public void PerArmScores_AreIgnored_OnlyRankMatters()
    {
        var first = Entry("first by rank");
        var second = Entry("second by rank");

        // Give the LOWER-ranked entry a wildly higher raw score — RRF must not care.
        var fused = RankFusion.ReciprocalRankFusion(
            keywordRanked: [Hit(first, score: 0.0001), Hit(second, score: 9999)],
            semanticRanked: [],
            limit: 10);

        fused[0].Entry.Id.Should().Be(first.Id, "fusion is rank-based, not score-based");
    }

    [Fact]
    public void EqualFusedScores_TieBreakByUpdatedAtDescending()
    {
        var older = Entry("older entry", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Entry("newer entry", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        // Same rank in different arms -> identical 1/(K+1) contribution.
        var fused = RankFusion.ReciprocalRankFusion(
            keywordRanked: [Hit(older)],
            semanticRanked: [Hit(newer)],
            limit: 10);

        fused[0].Entry.Id.Should().Be(newer.Id,
            "equal RRF scores break newest-first (ADR-016 running-log tie-break)");
        fused[0].Score.Should().BeApproximately(fused[1].Score, 1e-9);
    }

    [Fact]
    public void RecencyNeverOverridesARelevanceDifference()
    {
        var relevantButOld = Entry("rank 1, old", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lessRelevantButNew = Entry("rank 2, new", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var fused = RankFusion.ReciprocalRankFusion(
            keywordRanked: [Hit(relevantButOld), Hit(lessRelevantButNew)],
            semanticRanked: [],
            limit: 10);

        fused[0].Entry.Id.Should().Be(relevantButOld.Id,
            "recency is a tie-break only — it must never reorder distinct fused scores");
    }

    [Fact]
    public void Limit_TruncatesAfterFusion()
    {
        var entries = Enumerable.Range(0, 5).Select(i => Entry($"entry {i}")).ToList();

        var fused = RankFusion.ReciprocalRankFusion(
            keywordRanked: entries.Select(e => Hit(e)).ToList(),
            semanticRanked: [],
            limit: 2);

        fused.Should().HaveCount(2);
        fused[0].Entry.Id.Should().Be(entries[0].Id);
        fused[1].Entry.Id.Should().Be(entries[1].Id);
    }

    [Fact]
    public void EmptyArms_YieldEmptyResult()
    {
        RankFusion.ReciprocalRankFusion([], [], limit: 10).Should().BeEmpty();
    }
}
