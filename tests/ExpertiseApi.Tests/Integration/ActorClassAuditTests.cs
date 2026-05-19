using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Models;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// Integration coverage for Part D C6: the audit row's <c>ActorClass</c> /
/// <c>AuthMethod</c> / <c>ActorClassHeader</c> fields are populated end-to-end through the
/// authentication pipeline, the <c>?actorClass=</c> filter on <c>/audit</c> honours the
/// resolver's classification, and the admin-only <c>/audit/{id}/raw</c> escape hatch
/// returns the unmodified row.
/// </summary>
[Collection("Postgres")]
public class ActorClassAuditTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private JwtApiFactory _baseFactory = null!;

    public ActorClassAuditTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _baseFactory = new JwtApiFactory(_postgres.ConnectionString);
        // Disable deduplication for this class: the test factory's mock embedding
        // generator returns the SAME vector for every input, so the dedup service
        // would collapse every distinct test entry onto the first one's id (via 409
        // Conflict). We need real distinct entries to assert per-row audit attribution.
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deduplication:Enabled", "false");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
    }

    private static readonly object CreatePayload = new
    {
        domain = "shared",
        title = "C6 audit fixture entry",
        body = "Body content for an actor-class audit-tag test.",
        entryType = "Pattern",
        severity = "Info",
        source = "test"
    };

    private HttpClient MakeClient(IEnumerable<string> scopes, string? actorClassHeader = null, string? userAgent = null)
    {
        var token = JwtTokenMinter.Mint(
            tenant: "test",
            scopes: scopes,
            groups: ["group-test"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (actorClassHeader is not null)
            client.DefaultRequestHeaders.Add(AuthConstants.Headers.ActorClass, actorClassHeader);
        if (userAgent is not null)
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>
    /// Builds a payload with a unique body per call so the deduplication service does
    /// not collapse parallel test entries onto a single existing row (which would make
    /// each test see another test's audit-row ActorClass via the Conflict branch).
    /// </summary>
    private static object UniquePayload(string testTag) => new
    {
        domain = "shared",
        title = $"C6 audit fixture {testTag} {Guid.NewGuid():N}",
        body = $"Body content for an actor-class audit-tag test ({testTag} {Guid.NewGuid()}).",
        entryType = "Pattern",
        severity = "Info",
        source = "test"
    };

    private static async Task<Guid> CreateEntryAsync(HttpClient client, string testTag)
    {
        var response = await client.PostAsJsonAsync("/expertise", UniquePayload(testTag));
        // Conflict is NOT acceptable here — if dedup hits, the audit row we'd inspect
        // belongs to whichever test won the create race, not this test. Fail loudly.
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            $"POST /expertise returned {(int)response.StatusCode} with body: {await response.Content.ReadAsStringAsync()}");

        var element = await response.Content.ReadJsonElementAsync();
        return element.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement[]> ListAuditAsync(string? actorClass = null)
    {
        var admin = MakeClient([AuthConstants.AdminScope]);
        var path = actorClass is null ? "/audit" : $"/audit?actorClass={actorClass}";
        var response = await admin.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadJsonElementAsync();
        return json.EnumerateArray().ToArray();
    }

    [Fact]
    public async Task AgentScope_TagsAuditRowAsAgent_AndPersistsHeader()
    {
        var client = MakeClient(
            [AuthConstants.WriteDraftScope, AuthConstants.AgentScope],
            actorClassHeader: "agent",
            userAgent: "pi-coding-agent/0.5.0");

        var id = await CreateEntryAsync(client, "test");

        var rows = await ListAuditAsync();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == id);

        row.GetProperty("actorClass").GetString().Should().Be("Agent");
        row.GetProperty("authMethod").GetString().Should().Be(AuthExtensions.BearerScheme);
        row.GetProperty("actorClassHeader").GetString().Should().Be("agent");
    }

    [Fact]
    public async Task NoHeader_NoAgentScope_DefaultsToHuman()
    {
        var client = MakeClient([AuthConstants.WriteDraftScope]);

        var id = await CreateEntryAsync(client, "test");

        var rows = await ListAuditAsync();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == id);
        row.GetProperty("actorClass").GetString().Should().Be("Human");
        row.TryGetProperty("actorClassHeader", out var hdr).Should().BeTrue();
        (hdr.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [Fact]
    public async Task AgentHeader_WithoutScopeOrUaMatch_FallsBackToHuman_ButPersistsRawHeader()
    {
        // No expertise.agent scope, UA doesn't match any allowlist pattern. Resolver
        // logs a warning and tags Human; raw header survives for forensic recovery.
        var client = MakeClient(
            [AuthConstants.WriteDraftScope],
            actorClassHeader: "agent",
            userAgent: "Mozilla/5.0 (something-unrelated)");

        var id = await CreateEntryAsync(client, "test");

        var rows = await ListAuditAsync();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == id);
        row.GetProperty("actorClass").GetString().Should().Be("Human");
        row.GetProperty("actorClassHeader").GetString().Should().Be("agent");
    }

    [Fact]
    public async Task AgentHeader_WithoutScope_ButUaMatchesAllowlist_TagsAgent()
    {
        // UA-only corroboration path: supports the dev-skill case (no scope provisioned).
        var client = MakeClient(
            [AuthConstants.WriteDraftScope],
            actorClassHeader: "agent",
            userAgent: "pi-coding-agent/0.5.0");

        var id = await CreateEntryAsync(client, "test");

        var rows = await ListAuditAsync();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == id);
        row.GetProperty("actorClass").GetString().Should().Be("Agent");
    }

    [Fact]
    public async Task ScopePrimary_HumanHeaderDoesNotDowngradeAgentScope()
    {
        // Compromised-harness scenario: token carries the agent scope but the harness
        // sends X-Actor-Class: human to hide in the human subset. Scope wins; row is
        // tagged Agent regardless. Raw header is preserved so the mismatch is visible.
        var client = MakeClient(
            [AuthConstants.WriteDraftScope, AuthConstants.AgentScope],
            actorClassHeader: "human",
            userAgent: "pi-coding-agent/0.5.0");

        var id = await CreateEntryAsync(client, "test");

        var rows = await ListAuditAsync();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == id);
        row.GetProperty("actorClass").GetString().Should().Be("Agent");
        row.GetProperty("actorClassHeader").GetString().Should().Be("human");
    }

    [Fact]
    public async Task ActorClassFilter_AgentReturnsOnlyAgentRows()
    {
        // Seed one agent, one human row.
        var agentClient = MakeClient(
            [AuthConstants.WriteDraftScope, AuthConstants.AgentScope],
            actorClassHeader: "agent",
            userAgent: "pi-coding-agent/0.5.0");
        var humanClient = MakeClient([AuthConstants.WriteDraftScope]);

        var agentId = await CreateEntryAsync(agentClient, "agent");
        var humanId = await CreateEntryAsync(humanClient, "human");

        var agentRows = await ListAuditAsync("Agent");
        agentRows.Select(r => r.GetProperty("entryId").GetGuid()).Should().Contain(agentId);
        agentRows.Select(r => r.GetProperty("entryId").GetGuid()).Should().NotContain(humanId);

        var humanRows = await ListAuditAsync("Human");
        humanRows.Select(r => r.GetProperty("entryId").GetGuid()).Should().Contain(humanId);
        humanRows.Select(r => r.GetProperty("entryId").GetGuid()).Should().NotContain(agentId);
    }

    [Fact]
    public async Task GetAuditRaw_AsAdmin_ReturnsRowByIdWithAllFields()
    {
        var client = MakeClient(
            [AuthConstants.WriteDraftScope, AuthConstants.AgentScope],
            actorClassHeader: "agent",
            userAgent: "pi-coding-agent/0.5.0");

        var entryId = await CreateEntryAsync(client, "test");

        // Fetch the audit row id via the list endpoint, then probe /audit/{id}/raw.
        var rows = await ListAuditAsync();
        var listed = rows.Single(r => r.GetProperty("entryId").GetGuid() == entryId);
        var auditId = listed.GetProperty("id").GetGuid();

        var admin = MakeClient([AuthConstants.AdminScope]);
        var response = await admin.GetAsync($"/audit/{auditId}/raw");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await response.Content.ReadJsonElementAsync();
        raw.GetProperty("id").GetGuid().Should().Be(auditId);
        raw.GetProperty("actorClass").GetString().Should().Be("Agent");
        raw.GetProperty("actorClassHeader").GetString().Should().Be("agent");
        raw.GetProperty("authMethod").GetString().Should().Be(AuthExtensions.BearerScheme);
    }

    [Fact]
    public async Task GetAuditRaw_UnknownId_Returns404()
    {
        var admin = MakeClient([AuthConstants.AdminScope]);
        var response = await admin.GetAsync($"/audit/{Guid.NewGuid()}/raw");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAuditRaw_AsNonAdmin_Returns403()
    {
        var client = MakeClient([AuthConstants.ReadScope]);
        var response = await client.GetAsync($"/audit/{Guid.NewGuid()}/raw");
        response.StatusCode.Should().BeOneOf(new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized });
    }
}
