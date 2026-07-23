using System.Text.Json;
using Microsoft.SemanticKernel.Connectors.Onnx;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ExpertiseApi.Tests.Evaluation;

/// <summary>
/// Golden-query retrieval evaluation harness (#425, recommendation 5 of
/// docs/research/2026-07-retrieval-assessment.md).
///
/// Seeds the corpus from <c>golden-set.json</c> with REAL bge-micro-v2 embeddings
/// (the normal test suite's content-derived mock cannot measure retrieval quality),
/// runs every golden query through keyword and semantic search, and reports
/// recall@5 / recall@10 / MRR@10 per mode plus every miss. Run with:
///
/// <code>
/// EXPERTISE_EVAL=1 dotnet test --filter "FullyQualifiedName~RetrievalEval"
/// </code>
///
/// The assertion floors are a catastrophic-regression tripwire, deliberately set
/// well below measured values — the REPORT is the product; compare it across
/// revisions before merging any retrieval change (hybrid RRF, model swap,
/// reranking). Raise the floors when measured values improve durably.
/// </summary>
[Collection("Postgres")]
public sealed class RetrievalEvalTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    private const string EvalTenant = "eval";
    private const int ReportDepth = 10;

    // Catastrophic-regression floors (see class doc). Measured 2026-07-22 on the
    // 30-query golden set (post query-prefix fix, PR #431): keyword recall@5/@10
    // 0.367 (hits identifier queries, returns empty for multi-word paraphrases —
    // websearch_to_tsquery ANDs all terms), semantic recall@5 0.967 / recall@10
    // 1.000 / MRR 0.894. The keyword-vs-semantic complementarity is the empirical
    // case for the hybrid RRF endpoint (#428) — measured post-fusion (ADR-016):
    // hybrid recall@5 1.000 / recall@10 1.000 / MRR 0.923, strictly better than
    // either arm alone.
    private const double KeywordRecall10Floor = 0.30;
    private const double SemanticRecall10Floor = 0.60;
    private const double HybridRecall10Floor = 0.60;

    private ServiceProvider? _onnxProvider;
    private ExpertiseDbContext _db = null!;
    private EmbeddingService _embedding = null!;
    private ExpertiseRepository _repo = null!;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("EXPERTISE_EVAL") != "1" || ModelFiles.ModelPath is null)
            return; // the [EvalFact] skip fires before the test body; nothing to set up.

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
    public async Task GoldenQueries_ReportAndFloor()
    {
        var golden = LoadGoldenSet();

        // ---- Seed corpus with real document embeddings (batched) ----
        var docTexts = golden.Corpus.Select(d => EmbeddingService.BuildInputText(d.Title, d.Body));
        var docVectors = await _embedding.GenerateBatchAsync(docTexts);
        for (var i = 0; i < golden.Corpus.Count; i++)
        {
            var doc = golden.Corpus[i];
            var entry = TestHelpers.SeedEntry(
                domain: doc.Domain,
                title: doc.Title,
                body: doc.Body,
                entryType: Enum.Parse<EntryType>(doc.EntryType),
                severity: Enum.Parse<Severity>(doc.Severity),
                tenant: EvalTenant);
            entry.Embedding = docVectors[i];
            _db.ExpertiseEntries.Add(entry);
        }
        await _db.SaveChangesAsync();

        // ---- Run every query through both modes ----
        var ctx = TestHelpers.CreateTenantContext(tenant: EvalTenant);
        var keyword = new ModeMetrics("keyword");
        var semantic = new ModeMetrics("semantic");
        var hybrid = new ModeMetrics("hybrid");

        foreach (var q in golden.Queries)
        {
            // Arms fetched at the hybrid candidate depth; per-mode metrics score the
            // top ReportDepth slice (the lists are ranked, so slicing == a lower limit).
            var keywordResults = await _repo.KeywordSearchAsync(q.Query, ctx, includeDeprecated: false, limit: RankFusion.CandidatePoolSize,
                domain: null, tags: null, entryType: null, severity: null, CancellationToken.None);
            keyword.Score(q, keywordResults.Take(ReportDepth).Select(r => r.Entry.Title).ToList());

            var queryVector = await _embedding.GenerateQueryEmbeddingAsync(q.Query);
            var semanticResults = await _repo.SemanticSearchAsync(queryVector, ctx, limit: RankFusion.CandidatePoolSize, includeDeprecated: false,
                domain: null, tags: null, entryType: null, severity: null, CancellationToken.None);
            semantic.Score(q, semanticResults.Take(ReportDepth).Select(r => r.Entry.Title).ToList());

            var fused = RankFusion.ReciprocalRankFusion(keywordResults, semanticResults, ReportDepth);
            hybrid.Score(q, fused.Select(r => r.Entry.Title).ToList());
        }

        // ---- Report ----
        output.WriteLine($"Golden set: {golden.Corpus.Count} corpus entries, {golden.Queries.Count} queries");
        output.WriteLine("");
        output.WriteLine($"{"mode",-10} {"recall@5",9} {"recall@10",10} {"MRR@10",8}");
        foreach (var m in new[] { keyword, semantic, hybrid })
            output.WriteLine($"{m.Name,-10} {m.RecallAt5,9:F3} {m.RecallAt10,10:F3} {m.Mrr,8:F3}");
        output.WriteLine("");

        foreach (var m in new[] { keyword, semantic, hybrid }.Where(m => m.Misses.Count > 0))
        {
            output.WriteLine($"-- {m.Name}: {m.Misses.Count} miss(es) at depth {ReportDepth} --");
            foreach (var (query, kind, top) in m.Misses)
                output.WriteLine($"  [{kind}] \"{query}\" -> top-3: {string.Join(" | ", top.Take(3))}");
            output.WriteLine("");
        }

        // ---- Catastrophic-regression floors only (see class doc) ----
        keyword.RecallAt10.Should().BeGreaterThanOrEqualTo(KeywordRecall10Floor,
            "keyword retrieval collapsing below the floor indicates a broken query path, not normal variation");
        semantic.RecallAt10.Should().BeGreaterThanOrEqualTo(SemanticRecall10Floor,
            "semantic retrieval collapsing below the floor indicates a broken embedding or query path");
        hybrid.RecallAt10.Should().BeGreaterThanOrEqualTo(HybridRecall10Floor,
            "hybrid fusion collapsing below the floor indicates a broken fusion or arm path");
    }

    private static readonly JsonSerializerOptions GoldenSetJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static GoldenSet LoadGoldenSet()
    {
        var path = Path.Join(AppContext.BaseDirectory, "Evaluation", "golden-set.json");
        var json = File.ReadAllText(path);
        var set = JsonSerializer.Deserialize<GoldenSet>(json, GoldenSetJsonOptions);
        set.Should().NotBeNull();
        set!.Queries.Should().NotBeEmpty();

        // Every expected title must exist in the corpus — a typo here would silently
        // deflate every metric.
        var corpusTitles = set.Corpus.Select(c => c.Title).ToHashSet(StringComparer.Ordinal);
        foreach (var q in set.Queries)
            foreach (var expected in q.ExpectedTitles)
                corpusTitles.Should().Contain(expected,
                    $"query \"{q.Query}\" expects a title that is not in the corpus");

        return set;
    }

    private sealed class ModeMetrics(string name)
    {
        private int _queries;
        private int _hitsAt5;
        private int _hitsAt10;
        private double _reciprocalRankSum;

        public string Name { get; } = name;
        public List<(string Query, string Kind, List<string> Top)> Misses { get; } = [];

        public double RecallAt5 => _queries == 0 ? 0 : (double)_hitsAt5 / _queries;
        public double RecallAt10 => _queries == 0 ? 0 : (double)_hitsAt10 / _queries;
        public double Mrr => _queries == 0 ? 0 : _reciprocalRankSum / _queries;

        public void Score(EvalQuery query, List<string> rankedTitles)
        {
            _queries++;
            var firstRelevant = rankedTitles.FindIndex(t => query.ExpectedTitles.Contains(t, StringComparer.Ordinal));
            if (firstRelevant is >= 0 and < 5)
                _hitsAt5++;
            if (firstRelevant >= 0)
            {
                _hitsAt10++;
                _reciprocalRankSum += 1.0 / (firstRelevant + 1);
            }
            else
            {
                Misses.Add((query.Query, query.Kind, rankedTitles));
            }
        }
    }

    internal sealed record GoldenSet(List<EvalDoc> Corpus, List<EvalQuery> Queries);

    internal sealed record EvalDoc(string Domain, string Title, string Body, string EntryType, string Severity);

    internal sealed record EvalQuery(string Query, string Kind, List<string> ExpectedTitles);
}
