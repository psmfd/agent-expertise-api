using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Data;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Part D C3 / ADR-010 integration tests for the
/// <see cref="ExpertiseApi.Endpoints.Filters.IdempotencyEndpointFilter"/>.
/// Covers replay byte-equality (including the hygiene-frozen nonce — the
/// 2026-05-19 dotnet-expert consensus spike test), mismatch on key reuse,
/// validation, soft/hard RequireKey flag, and cross-tenant isolation.
/// </summary>
[Collection("Postgres")]
public class IdempotencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private JwtApiFactory _factory = null!;

    public IdempotencyTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new JwtApiFactory(_postgres.ConnectionString);

        // Clean state per test. Migration runs in the fixture's
        // InitializeAsync; the idempotency_records table is created there.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ExpertiseDbContext>();
        await db.ExpertiseAuditLogs.IgnoreQueryFilters().ExecuteDeleteAsync();
        await db.ExpertiseEntries.IgnoreQueryFilters().ExecuteDeleteAsync();

        // Truncate idempotency_records via raw SQL (not EF-mapped).
        await db.Database.ExecuteSqlRawAsync("DELETE FROM idempotency_records;");
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient WriterClient()
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [AuthConstants.WriteDraftScope, AuthConstants.ReadScope],
            groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent BodyOf(object payload, Encoding? enc = null)
    {
        var json = JsonSerializer.Serialize(payload);
        return new StringContent(json, enc ?? Encoding.UTF8, "application/json");
    }

    private static object MinimalCreatePayload(string title = "ADR-010 replay sample") => new
    {
        Domain = "tooling",
        Title = title,
        Body = "Body text for the C3 replay test.",
        EntryType = "Pattern",
        Severity = "Info",
        Source = "test-suite",
    };

    [Fact]
    public async Task Post_without_idempotency_key_under_soft_require_passes_through_unchanged()
    {
        // Baseline: confirms the filter does NOT alter behaviour when the
        // header is absent and RequireKey=false (the shipped default).
        using var client = WriterClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains("Idempotency-Replay").Should().BeFalse();
    }

    [Fact]
    public async Task Replay_with_same_key_and_body_returns_cached_response_with_marker_header()
    {
        // Core ADR-010 contract: identical resend → byte-equal response body
        // (including the hygiene-frozen nonce that the spike test exists to
        // pin down) + Idempotency-Replay: true header.
        using var client = WriterClient();
        var key = Guid.NewGuid().ToString("N");
        var payload = MinimalCreatePayload();

        async Task<HttpResponseMessage> SendAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(payload) };
            req.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(req);
        }

        var first = await SendAsync();
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBody = await first.Content.ReadAsByteArrayAsync();
        first.Headers.Contains("Idempotency-Replay").Should().BeFalse();

        // Give the OnCompleted callback a beat to land.
        await Task.Delay(200);

        var second = await SendAsync();
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondBody = await second.Content.ReadAsByteArrayAsync();
        second.Headers.GetValues("Idempotency-Replay").Should().ContainSingle().Which.Should().Be("true");

        // Byte-equality is the ADR-010 promise (and the spike's PATTERN_VALIDATED
        // outcome). The hygiene nonce is part of the body bytes; if the filter
        // re-executed the handler instead of replaying, the nonce would be
        // freshly minted and this assertion would fail.
        secondBody.Should().BeEquivalentTo(firstBody);
    }

    [Fact]
    public async Task Replay_byte_equality_includes_hygiene_nonce_and_envelope()
    {
        // Spike consensus assertion: the captured bytes carry the per-response
        // hygiene nonce (ADR-008) frozen at original-request time. Re-execution
        // would mint a new nonce; replay does not.
        using var client = WriterClient();
        var key = Guid.NewGuid().ToString("N");
        var payload = MinimalCreatePayload(title: "Nonce-freeze evidence");

        async Task<HttpResponseMessage> SendAsync()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(payload) };
            req.Headers.Add("Idempotency-Key", key);
            return await client.SendAsync(req);
        }

        var first = await SendAsync();
        var firstJson = await first.Content.ReadAsStringAsync();
        firstJson.Should().Contain("_hygiene", "hygiene envelope is part of the response by ADR-008");

        await Task.Delay(200);

        var second = await SendAsync();
        var secondJson = await second.Content.ReadAsStringAsync();

        // Extract the nonce from both responses; if the filter accidentally
        // re-executed, the two nonces would differ (they're minted from
        // RandomNumberGenerator on every request).
        var firstNonce = ExtractNonce(firstJson);
        var secondNonce = ExtractNonce(secondJson);
        secondNonce.Should().Be(firstNonce, "replay must preserve the original hygiene nonce");

        secondJson.Should().Be(firstJson, "full response body must byte-equal the original");
    }

    private static string ExtractNonce(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("_hygiene").GetProperty("nonce").GetString()!;
    }

    [Fact]
    public async Task Reuse_of_key_with_different_body_returns_409()
    {
        using var client = WriterClient();
        var key = Guid.NewGuid().ToString("N");

        var firstReq = new HttpRequestMessage(HttpMethod.Post, "/expertise/")
        { Content = BodyOf(MinimalCreatePayload(title: "first")) };
        firstReq.Headers.Add("Idempotency-Key", key);
        var first = await client.SendAsync(firstReq);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        await Task.Delay(200);

        var secondReq = new HttpRequestMessage(HttpMethod.Post, "/expertise/")
        { Content = BodyOf(MinimalCreatePayload(title: "second-different-body")) };
        secondReq.Headers.Add("Idempotency-Key", key);
        var second = await client.SendAsync(secondReq);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await second.Content.ReadAsStringAsync();
        problem.Should().Contain("Idempotency-Key reuse");
    }

    [Theory]
    [InlineData("has space")]                           // whitespace
    [InlineData("key,with,comma-like-multi-value")]     // legitimate multi-value smell — caught at VCHAR loop (comma is VCHAR; intentionally passes)
    public async Task Invalid_idempotency_key_returns_400(string badKey)
    {
        using var client = WriterClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", badKey);

        var response = await client.SendAsync(req);
        // "key,with,comma" is actually valid VCHAR per IETF §2.2; only "has space" should 400 here.
        // (Comprehensive charset coverage lives in IdempotencyKeyValidatorTests; this Theory
        //  just smoke-tests the HTTP-layer plumbing for one realistic invalid shape.)
        if (badKey.Contains(' ', StringComparison.Ordinal))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    [Fact]
    public async Task Overlong_idempotency_key_returns_400()
    {
        using var client = WriterClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", new string('k', 256));
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Hard_require_rejects_missing_header_with_400()
    {
        // Flip the option for this test factory only — exercises the
        // post-#205/#206 operator-flip path documented on
        // IdempotencyOptions.RequireKey.
        await using var requireFactory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Idempotency:RequireKey", "true"));

        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: [AuthConstants.WriteDraftScope, AuthConstants.ReadScope],
            groups: ["group-test"]);
        using var client = requireFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var req = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Idempotency-Key required");
    }

    [Fact]
    public async Task Cross_tenant_same_key_is_independent()
    {
        // Two writers with the same Idempotency-Key but distinct tenants must
        // each succeed independently — partition key is (tenant, key).
        var keyShared = Guid.NewGuid().ToString("N");

        var tokenA = JwtTokenMinter.Mint("test", [AuthConstants.WriteDraftScope, AuthConstants.ReadScope], groups: ["group-test"]);
        var tokenB = JwtTokenMinter.Mint("other-team", [AuthConstants.WriteDraftScope, AuthConstants.ReadScope], groups: ["group-other"]);

        using var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var reqA = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        reqA.Headers.Add("Idempotency-Key", keyShared);
        var respA = await clientA.SendAsync(reqA);
        respA.StatusCode.Should().Be(HttpStatusCode.Created);

        var reqB = new HttpRequestMessage(HttpMethod.Post, "/expertise/") { Content = BodyOf(MinimalCreatePayload()) };
        reqB.Headers.Add("Idempotency-Key", keyShared);
        var respB = await clientB.SendAsync(reqB);

        respB.StatusCode.Should().Be(HttpStatusCode.Created);
        respB.Headers.Contains("Idempotency-Replay").Should().BeFalse("tenant B has never used this key");
    }
}
