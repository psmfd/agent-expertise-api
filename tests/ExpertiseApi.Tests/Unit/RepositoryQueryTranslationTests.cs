using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// DB-less EF Core query-translation guard (#352). <c>ToQueryString()</c> runs the full
/// EF+Npgsql translation pipeline WITHOUT a database or open connection — the exact probe
/// that would have caught the <c>FindExactMatchesAsync</c> <c>ToLowerInvariant()</c> bug
/// (compiled clean, analyzer-clean, threw only at runtime against a real provider).
/// <para>
/// Every distinctive predicate shape in <see cref="ExpertiseRepository"/> gets a
/// NotThrow assertion. Simple equality/comparison filters (tenant, id) always translate
/// and are covered incidentally through the shared seams; the shapes with real
/// translation risk are called out per test: array containment (<c>tags.All</c>),
/// <c>LOWER()</c> (<c>ToLower</c>), pgvector <c>CosineDistance</c> ordering, the two Guid
/// keyset-cursor forms (<c>&gt;</c> vs <c>CompareTo</c>), and <c>HasConversion&lt;string&gt;</c>
/// enum comparisons.
/// </para>
/// <para>
/// SAME-PR EXPECTATION: any new conditional filter or <c>EF.Functions.*</c> call added to
/// <see cref="ExpertiseRepository"/> ships with a translation assertion here. This suite
/// is fast (no Docker) — there is no excuse to skip it.
/// </para>
/// </summary>
public class RepositoryQueryTranslationTests
{
    // No database is contacted: ToQueryString generates SQL from the model + provider
    // without opening a connection ("the database does not need to exist"). The
    // connection string only needs to be parseable.
    private static ExpertiseDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ExpertiseDbContext>()
            .UseNpgsql("Host=localhost;Database=translate-only;Username=x;Password=x", o => o.UseVector())
            .Options;
        return new ExpertiseDbContext(options, new NoOpTenantContextAccessor());
    }

    private static ExpertiseRepository NewRepository(ExpertiseDbContext db) =>
        new(db, new HttpContextAccessor(), NullLogger<ExpertiseRepository>.Instance);

    private static TenantContext Ctx() =>
        new("team-alpha", new ClaimsPrincipal(), Agent: null, new HashSet<string>());

    private static void AssertTranslates(IQueryable query, string because)
    {
        var act = () => query.ToQueryString();
        act.Should().NotThrow(because);
    }

    // ---- ListAsync — combinatorial conditional filters (highest-risk method) ----

    [Fact]
    public void ListQuery_EveryFilterCombination_Translates()
    {
        using var db = NewContext();
        var repo = NewRepository(db);
        var tags = new List<string> { "postgres", "ef-core" };

        // 5 conditional dimensions -> 32 combinations, exercised against the REAL
        // BuildListQuery seam (extracted from ListAsync) so this can never drift from
        // production. The tags.All(t => e.Tags.Contains(t)) array-containment predicate
        // over a text[] column is the same shape-class as the ToLowerInvariant bug.
        for (var mask = 0; mask < 32; mask++)
        {
            var query = repo.BuildListQuery(
                Ctx(),
                domain: (mask & 1) != 0 ? "dotnet" : null,
                tags: (mask & 2) != 0 ? tags : null,
                entryType: (mask & 4) != 0 ? EntryType.Pattern : null,
                severity: (mask & 8) != 0 ? Severity.Warning : null,
                includeDeprecated: (mask & 16) != 0);

            AssertTranslates(query, $"ListAsync filter mask {mask} must translate to SQL");
        }
    }

    // ---- LOWER() predicates (the shipped-bug shape) -----------------------

    [Fact]
    [SuppressMessage("Performance", "CA1862:Use the StringComparison overload of String.Equals",
        Justification = "This is a LINQ-to-SQL expression tree, not a runtime comparison — e.Title.ToLower() must translate to SQL LOWER() and a StringComparison overload does not translate. Mirrors the production suppression on FindExactMatchAsync.")]
    public void FindExactMatch_LowerEquality_Translates()
    {
        using var db = NewContext();
        var lowerTitle = "some title";
        var query = ExpertiseRepository.ApplyTenantFilter(db.ExpertiseEntries, Ctx())
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == "dotnet")
            .Where(e => e.Title.ToLower() == lowerTitle);

        AssertTranslates(query, "e.Title.ToLower() must map to SQL LOWER() (ToLowerInvariant does not translate)");
    }

    [Fact]
    public void FindExactMatches_ContainsLower_Translates()
    {
        using var db = NewContext();
        var lowerTitles = new List<string> { "a", "b" };
        var query = ExpertiseRepository.ApplyTenantFilter(db.ExpertiseEntries, Ctx())
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == "dotnet")
            .Where(e => lowerTitles.Contains(e.Title.ToLower()));

        AssertTranslates(query, "lowerTitles.Contains(e.Title.ToLower()) is the exact shape that shipped broken as ToLowerInvariant");
    }

    // ---- pgvector CosineDistance ordering ---------------------------------

    [Fact]
    public void SemanticSearch_CosineDistanceOrdering_Translates()
    {
        using var db = NewContext();
        var vector = new Vector(new float[384]);
        var query = ExpertiseRepository.ApplyApprovedReviewFilter(
                ExpertiseRepository.ApplyTenantFilter(db.ExpertiseEntries, Ctx()))
            .Where(e => e.Embedding != null)
            .Where(e => e.DeprecatedAt == null)
            .OrderBy(e => e.Embedding!.CosineDistance(vector))
            .Take(10);

        AssertTranslates(query, "pgvector CosineDistance ORDER BY must translate");
    }

    [Fact]
    public void FindNearestInDomain_CosineDistanceOrdering_Translates()
    {
        using var db = NewContext();
        var vector = new Vector(new float[384]);
        var query = ExpertiseRepository.ApplyTenantFilter(db.ExpertiseEntries, Ctx())
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.ReviewState != ReviewState.Rejected)
            .Where(e => e.Domain == "dotnet")
            .Where(e => e.Embedding != null)
            .OrderBy(e => e.Embedding!.CosineDistance(vector));

        AssertTranslates(query, "dedup nearest-neighbour CosineDistance ORDER BY must translate");
    }

    // ---- Guid keyset cursors (two distinct comparison forms) --------------

    [Fact]
    public void SharedApprovedKeyset_GuidGreaterThan_Translates()
    {
        using var db = NewContext();
        var afterId = Guid.NewGuid();
        var afterUpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var query = db.ExpertiseEntries
            .Where(e => e.Tenant == "shared")
            .Where(e => e.ReviewState == ReviewState.Approved)
            .Where(e => e.DeprecatedAt == null)
            .Where(e => e.UpdatedAt > afterUpdatedAt
                        || (e.UpdatedAt == afterUpdatedAt && e.Id > afterId))
            .OrderBy(e => e.UpdatedAt).ThenBy(e => e.Id)
            .Take(100);

        AssertTranslates(query, "up-sync keyset uses the Guid > operator (uuid comparison)");
    }

    [Fact]
    public void AuditQuery_AllFiltersAndCompareToCursor_Translate()
    {
        using var db = NewContext();
        var cursorTs = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cursorId = Guid.NewGuid();
        var query = db.ExpertiseAuditLogs.AsQueryable()
            .Where(a => a.EntryId == Guid.NewGuid())
            .Where(a => a.Principal == "p")
            .Where(a => a.Action == AuditAction.Created)
            .Where(a => a.ActorClass == ActorClass.Service)
            .Where(a => a.Timestamp >= cursorTs)
            .Where(a => a.Timestamp <= cursorTs)
            .Where(a => a.Timestamp < cursorTs
                        || (a.Timestamp == cursorTs && a.Id.CompareTo(cursorId) < 0))
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Take(50);

        AssertTranslates(query, "audit keyset uses Guid.CompareTo — a distinct translation from the > operator");
    }

    // ---- Enum disjunction + draft queue -----------------------------------

    [Fact]
    public void ListDrafts_ReviewStateDisjunction_Translates()
    {
        using var db = NewContext();
        var query = db.ExpertiseEntries
            .Where(e => e.Tenant == "team-alpha")
            .Where(e => e.ReviewState == ReviewState.Draft || e.ReviewState == ReviewState.Rejected)
            .Where(e => e.DeprecatedAt == null)
            .OrderByDescending(e => e.UpdatedAt);

        AssertTranslates(query, "HasConversion<string> enum disjunction must translate");
    }

    // ---- Approved-read seam (GetById / SemanticSearch prefix) -------------

    [Fact]
    public void ApprovedTenantSeam_Translates()
    {
        using var db = NewContext();
        var query = ExpertiseRepository.ApplyApprovedReviewFilter(
                ExpertiseRepository.ApplyTenantFilter(db.ExpertiseEntries, Ctx()))
            .Where(e => e.Id == Guid.NewGuid());

        AssertTranslates(query, "the shared tenant+approved read seam must translate");
    }

    // ---- Raw FromSqlInterpolated (keyword search) -------------------------

    [Fact]
    public void KeywordSearch_FromSqlInterpolated_Parameterizes()
    {
        using var db = NewContext();
        var q = "postgres pgvector";
        var tenant = "team-alpha";
        var approvedState = nameof(ReviewState.Approved);

        // ToQueryString on FromSql renders the raw SQL with its parameters — this pins the
        // interpolation/parameterization (it does not validate the tsquery against a live
        // server; SearchEndpointTests covers execution correctness).
        var limit = 50;
        var query = db.ExpertiseEntries.FromSqlInterpolated($"""
            SELECT *, xmin FROM "ExpertiseEntries"
            WHERE "SearchVector" @@ websearch_to_tsquery('english', {q})
              AND ("Tenant" = {tenant} OR "Tenant" = 'shared')
              AND "ReviewState" = {approvedState}
              AND "DeprecatedAt" IS NULL
            ORDER BY ts_rank_cd("SearchVector", websearch_to_tsquery('english', {q})) DESC
            LIMIT {limit}
            """);

        AssertTranslates(query, "keyword-search raw SQL must parameterize without throwing");
    }
}
