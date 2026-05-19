using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpertiseApi.Auth;
using ExpertiseApi.Models;
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
            Embedding = CreateTestVector(),
            Tenant = tenant,
            AuthorPrincipal = authorPrincipal,
            ReviewState = reviewState,
            Visibility = visibility
        };
    }

    internal static Vector CreateTestVector(int dimensions = 384)
    {
        var values = new float[dimensions];
        var rng = new Random(42);
        for (var i = 0; i < dimensions; i++)
            values[i] = (float)(rng.NextDouble() * 2 - 1);
        return new Vector(values);
    }
}
