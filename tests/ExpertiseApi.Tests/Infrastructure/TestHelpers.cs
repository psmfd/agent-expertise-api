using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using ExpertiseApi.Services;
using Pgvector;

namespace ExpertiseApi.Tests.Infrastructure;

internal static class TestHelpers
{
    internal const string TestApiKey = "test-api-key-12345";
    internal const string TestTenant = "test";

    /// <summary>
    /// Builds a <see cref="TenantContext"/> for direct repository calls in tests
    /// (i.e., outside the HTTP request pipeline). Defaults to read+draft scopes;
    /// callers that need approve/admin can pass them explicitly.
    /// </summary>
    internal static TenantContext CreateTenantContext(
        string tenant = TestTenant,
        params string[] scopes)
    {
        var scopeSet = scopes.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                AuthConstants.ReadScope,
                AuthConstants.WriteDraftScope
            }
            : new HashSet<string>(scopes, StringComparer.Ordinal);

        var identity = new ClaimsIdentity(
            new[] { new Claim("sub", $"test-{tenant}") }, "Test");
        return new TenantContext(
            Tenant: tenant,
            Principal: new ClaimsPrincipal(identity),
            Agent: null,
            Scopes: scopeSet);
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<T?> ReadJsonAsync<T>(this HttpContent content)
        => await content.ReadFromJsonAsync<T>(JsonOptions);

    internal static async Task<JsonElement> ReadJsonElementAsync(this HttpContent content)
    {
        var stream = await content.ReadAsStreamAsync();
        var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    internal static HttpClient CreateAuthenticatedClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    internal static HttpClient CreateUnauthenticatedClient(ApiFactory factory)
        => factory.CreateClient();

    internal static ExpertiseEntry SeedEntry(
        string domain = "shared",
        string title = "Test entry",
        string body = "Test body content for search indexing",
        EntryType entryType = EntryType.Pattern,
        Severity severity = Severity.Info,
        string source = "test",
        string tenant = TestTenant,
        string authorPrincipal = "test-principal",
        ReviewState reviewState = ReviewState.Approved,
        Visibility visibility = Visibility.Private)
    {
        return new ExpertiseEntry
        {
            Domain = domain,
            Title = title,
            Body = body,
            EntryType = entryType,
            Severity = severity,
            Source = source,
            Tags = ["test"],
            // Content-derived so a seeded entry and a POSTed entry with the same
            // title+body produce the SAME embedding (semantic dedup can then collide
            // them), while distinct content stays near-orthogonal (#353).
            Embedding = CreateContentVector(EmbeddingService.BuildInputText(title, body)),
            Tenant = tenant,
            AuthorPrincipal = authorPrincipal,
            ReviewState = reviewState,
            Visibility = visibility
        };
    }

    /// <summary>Content-independent fixed vector (query vectors / unit-test stubs that don't care).</summary>
    internal static Vector CreateTestVector(int dimensions = 384)
    {
        var values = new float[dimensions];
        var rng = new Random(42);
        for (var i = 0; i < dimensions; i++)
            values[i] = (float)(rng.NextDouble() * 2 - 1);
        return new Vector(values);
    }

    /// <summary>
    /// Deterministic, CONTENT-DERIVED embedding for tests (#353). Identical content yields
    /// the identical vector (cosine distance 0 → a real duplicate); different content yields
    /// a near-orthogonal vector (cosine distance ≈ 1.0, far above the 0.10 dedup threshold →
    /// NOT a duplicate). Replaces the old content-independent mock that returned the same
    /// vector for every input — which made semantic-dedup behaviour structurally unobservable
    /// and forced two integration tests into per-test workarounds.
    /// </summary>
    internal static float[] CreateContentEmbedding(string content, int dimensions = 384)
    {
        var rng = new Random(StableSeed(content));
        var values = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
            values[i] = (float)(rng.NextDouble() * 2 - 1);
        return values;
    }

    internal static Vector CreateContentVector(string content, int dimensions = 384)
        => new(CreateContentEmbedding(content, dimensions));

    // FNV-1a over UTF-16 code units. Stable across processes, unlike string.GetHashCode
    // (randomized per run in .NET Core) — the seed MUST be reproducible so the same content
    // embeds identically in the mock generator and in seeded fixtures. Collisions are
    // irrelevant for test data.
    private static int StableSeed(string s)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in s)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
