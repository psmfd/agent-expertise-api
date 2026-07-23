using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Microsoft.SemanticKernel.Connectors.Onnx;
using Xunit.Abstractions;

namespace ExpertiseApi.Tests.Evaluation;

/// <summary>
/// Needle-in-document long-context retrieval gate (#437). Eight synthetic
/// ~8-10k-char incident-review documents, each carrying one distinctive fact
/// (the "needle") at a controlled character depth (two docs each at ~400 /
/// ~2500 / ~5000 / ~8000 chars), seeded through the real repository search
/// paths. Each query targets exactly one needle.
///
/// The floors are WINDOW-AWARE: only needles whose worst-case token position
/// fits inside <see cref="EmbeddingModelInfo.MaximumTokens"/> are asserted, so
/// the suite is meaningful at any ceiling — at 512 tokens only the shallow
/// needles gate; raising the ceiling (#437) activates the deeper ones with no
/// test change. Out-of-window needles are still reported (the report is the
/// product, as in <see cref="RetrievalEvalTests"/>); they are expected to rank
/// poorly under a small window — that gap is the empirical case for the swap.
///
/// Deterministic construction: fixed filler paragraphs cycled by index, no RNG.
/// Corpus and methodology are ported from the #437 spike (issue #437, findings
/// comments of 2026-07-23).
///
/// <code>
/// EXPERTISE_EVAL=1 dotnet test --filter "FullyQualifiedName~NeedleEval"
/// </code>
/// </summary>
[Collection("Postgres")]
public sealed class NeedleEvalTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private const string EvalTenant = "eval-needle";
    private const int ReportDepth = 10;

    // Worst-case token density measured on the 60 longest real corpus entries
    // (#429 derivation: 2.97–4.24 chars/token). Dividing a character position
    // by the MINIMUM observed density over-estimates its token position, so an
    // "in-window" classification here is conservative — a needle classified
    // in-window is genuinely inside the embedding window.
    private const double MinCharsPerToken = 2.9;

    // Headroom for the title + separator BuildInputText prepends, [CLS]/[SEP],
    // and tokenizer variance.
    private const int TokenMargin = 64;

    // Floors for IN-WINDOW needles only (catastrophic-regression tripwire, not
    // a target — see RetrievalEvalTests class doc). Spike-measured semantic
    // ranks for in-window needles were 1–2 across bge-micro-v2@512 (shallow
    // needles) and jina-v2-small@6144 (all depths, avg rank 1.5).
    private const double SemanticAvgRankCeiling = 3.0;
    private const int SemanticWorstRankCeiling = 5;

    private ServiceProvider? _onnxProvider;
    private ExpertiseDbContext _db = null!;
    private EmbeddingService _embedding = null!;
    private ExpertiseRepository _repo = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EXPERTISE_EVAL") != "1" || ModelFiles.ModelPath is null)
            return;

        var services = new ServiceCollection();
        services.AddBertOnnxEmbeddingGenerator(ModelFiles.ModelPath!, ModelFiles.VocabPath!,
            new BertOnnxOptions { MaximumTokens = EmbeddingModelInfo.MaximumTokens });
        _onnxProvider = services.BuildServiceProvider();
        _embedding = new EmbeddingService(
            _onnxProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());

        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql(postgres.ConnectionString, o => o.UseVector())
            .Options;
        _db = new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
        _repo = new ExpertiseRepository(_db, new HttpContextAccessor(), NullLogger<ExpertiseRepository>.Instance);

        await _db.ExpertiseEntries.Where(e => e.Tenant == EvalTenant).ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.ExpertiseEntries.Where(e => e.Tenant == EvalTenant).ExecuteDeleteAsync();
            await _db.DisposeAsync();
        }
        _onnxProvider?.Dispose();
    }

    [EvalFact]
    public async Task NeedleQueries_ReportAndWindowAwareFloor()
    {
        var docs = NeedleCorpus.Build();

        // ---- Seed through the real entry shape with real embeddings ----
        var docTexts = docs.Select(d => EmbeddingService.BuildInputText(d.Title, d.Body));
        var docVectors = await _embedding.GenerateBatchAsync(docTexts);
        for (var i = 0; i < docs.Count; i++)
        {
            var entry = TestHelpers.SeedEntry(
                domain: "needle-eval", title: docs[i].Title, body: docs[i].Body, tenant: EvalTenant);
            entry.Embedding = docVectors[i];
            _db.ExpertiseEntries.Add(entry);
        }
        await _db.SaveChangesAsync();

        // ---- Rank every needle query in all three modes ----
        var ctx = TestHelpers.CreateTenantContext(tenant: EvalTenant);
        var results = new List<(NeedleDoc Doc, bool InWindow, int Semantic, int Keyword, int Hybrid)>();

        foreach (var doc in docs)
        {
            var inWindow = ((doc.NeedleEndChars / MinCharsPerToken) + TokenMargin)
                < EmbeddingModelInfo.MaximumTokens;

            var queryVector = await _embedding.GenerateQueryEmbeddingAsync(doc.Query);
            var semanticResults = await _repo.SemanticSearchAsync(queryVector, ctx, limit: ReportDepth, includeDeprecated: false,
                domain: null, tags: null, entryType: null, severity: null, CancellationToken.None);
            var keywordResults = await _repo.KeywordSearchAsync(doc.Query, ctx, includeDeprecated: false, limit: ReportDepth,
                domain: null, tags: null, entryType: null, severity: null, CancellationToken.None);
            var fused = RankFusion.ReciprocalRankFusion(keywordResults, semanticResults, ReportDepth);

            results.Add((doc, inWindow,
                RankOf(semanticResults.Select(r => r.Entry.Title), doc.Title),
                RankOf(keywordResults.Select(r => r.Entry.Title), doc.Title),
                RankOf(fused.Select(r => r.Entry.Title), doc.Title)));
        }

        // ---- Report ----
        output.WriteLine($"Needle corpus: {docs.Count} docs, ceiling {EmbeddingModelInfo.MaximumTokens} tokens " +
            $"(in-window: {results.Count(r => r.InWindow)}/{docs.Count}); rank 0 = not in top {ReportDepth}");
        output.WriteLine("");
        output.WriteLine($"{"doc",-28} {"depth",6} {"window",7} {"sem",4} {"kw",4} {"hyb",4}");
        foreach (var r in results)
            output.WriteLine($"{r.Doc.Title,-28} {r.Doc.DepthChars,6} {(r.InWindow ? "in" : "out"),7} {r.Semantic,4} {r.Keyword,4} {r.Hybrid,4}");
        output.WriteLine("");

        // ---- Window-aware floors (semantic arm — the capability under test) ----
        var inWindowResults = results.Where(r => r.InWindow).ToList();
        inWindowResults.Should().NotBeEmpty(
            "the window-gating math must leave at least the shallow needles asserted at any plausible ceiling");

        foreach (var r in inWindowResults)
        {
            r.Semantic.Should().BeInRange(1, SemanticWorstRankCeiling,
                $"in-window needle '{r.Doc.Title}' (depth {r.Doc.DepthChars} chars) must be semantically retrievable");
        }

        var avgRank = inWindowResults.Average(r => (double)r.Semantic);
        avgRank.Should().BeLessThanOrEqualTo(SemanticAvgRankCeiling,
            "in-window needles collapsing on average indicates a broken embedding path or a ceiling regression");
    }

    private static int RankOf(IEnumerable<string> rankedTitles, string title)
    {
        var idx = rankedTitles.ToList().FindIndex(t => string.Equals(t, title, StringComparison.Ordinal));
        return idx + 1; // 0 = miss
    }

    internal sealed record NeedleDoc(string Title, string Body, string Query, int DepthChars, int NeedleEndChars);

    /// <summary>
    /// Deterministic needle-document builder ported from the #437 spike
    /// (fixed filler paragraphs cycled by seed index; no randomness).
    /// </summary>
    internal static class NeedleCorpus
    {
        private static readonly string[] Filler =
        [
            "During the incident review, the on-call engineer confirmed that the alerting pipeline correctly paged the secondary responder within the expected two-minute window, and the runbook link attached to the page resolved without redirect errors. ",
            "The service mesh sidecar proxies reported steady p99 latency throughout the affected window, which initially misled the responding team into ruling out network-layer causes before the eventual root cause was isolated. ",
            "Capacity planning for the quarter had already flagged the affected cluster as running close to its provisioned memory ceiling, though the on-call rotation had not yet actioned the recommended node pool expansion at the time of the incident. ",
            "Synthetic monitoring checks against the public API endpoints continued to pass green throughout the degradation, since the synthetic probes exercised a code path that happened not to intersect with the failing component. ",
            "The postmortem timeline was reconstructed from a combination of structured application logs, the deployment audit trail, and the on-call chat transcript, which together gave a minute-by-minute account of the response. ",
            "Several downstream consumers of the affected service implemented their own retry-with-backoff logic, which absorbed a portion of the failure and delayed customer-visible symptoms by roughly eleven minutes after the underlying fault began. ",
            "The engineering team maintains a shared dashboard of golden signals for every tier-one service, covering latency, traffic, errors and saturation, and this dashboard was the first place the responder looked once paged. ",
            "A follow-up action item was filed to add a synthetic check that specifically exercises the failure mode uncovered by this incident, closing the monitoring gap that let the issue reach production undetected. ",
            "The affected component had passed its most recent quarterly disaster-recovery exercise without any findings, which the review board noted as a gap in how those exercises are scoped going forward. ",
            "Escalation paging went through the secondary on-call rotation because the primary responder's phone was in do-not-disturb mode during a scheduled maintenance window unrelated to this incident. ",
        ];

        private static readonly (int Depth, string Needle, string Title, string Query, int Total, int Seed)[] Defs =
        [
            (400, "The incident was ultimately traced to a misconfigured retry budget of exactly seven hundred forty three milliseconds configured on the payment-gateway sidecar.",
                "incident review doc1", "What was the exact retry budget value that caused the payment gateway incident?", 8200, 0),
            (400, "Root cause: an expired TLS client certificate issued to the inventory-sync-worker service account expired at zero three fourteen UTC.",
                "incident review doc2", "Which service account had an expired certificate that caused the inventory sync failure?", 8600, 1),
            (2500, "The fix required rotating the Redis eviction policy from allkeys-lru to volatile-ttl on the session-cache cluster.",
                "incident review doc3", "What Redis eviction policy change fixed the session cache issue?", 9000, 2),
            (2500, "The regression was introduced by a commit that removed the idempotency check inside the checkout-reconciler job.",
                "incident review doc4", "What change introduced the checkout idempotency regression?", 9400, 3),
            (5000, "The database deadlock was caused by two migrations acquiring locks on the orders table and the order_items table in reverse order.",
                "incident review doc5", "What caused the database deadlock between the orders and order_items tables?", 9800, 4),
            (5000, "The outage was resolved by increasing the file descriptor limit from one thousand twenty four to sixty five thousand five hundred thirty six on the log-shipper daemonset.",
                "incident review doc6", "What file descriptor limit change fixed the log shipper outage?", 8400, 5),
            (8000, "The root cause was a clock skew of forty seven seconds between the auth-broker service and the token-validation service.",
                "incident review doc7", "How much clock skew caused the auth broker token validation issue?", 9200, 6),
            (8000, "The leak was traced to an unclosed gRPC channel inside the notification-dispatcher retry loop.",
                "incident review doc8", "What caused the gRPC channel leak in the notification dispatcher?", 8800, 7),
        ];

        public static List<NeedleDoc> Build() => Defs.Select(d =>
        {
            var before = FillerUpTo(d.Depth, d.Seed);
            if (before.Length > d.Depth)
                before = before[..d.Depth];
            var afterTarget = Math.Max(d.Total - before.Length - d.Needle.Length - 2, 0);
            var after = FillerUpTo(afterTarget, d.Seed + 5);
            if (after.Length > afterTarget)
                after = after[..afterTarget];
            var body = $"{before} {d.Needle} {after}";
            var needleEnd = before.Length + 1 + d.Needle.Length;
            return new NeedleDoc(d.Title, body, d.Query, d.Depth, needleEnd);
        }).ToList();

        private static string FillerUpTo(int targetChars, int startIdx)
        {
            var sb = new StringBuilder();
            var i = startIdx;
            while (sb.Length < targetChars)
            {
                sb.Append(Filler[i % Filler.Length]);
                i++;
            }
            return sb.ToString();
        }
    }
}
