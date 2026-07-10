using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-014/ADR-015 regression guard: an offline-minted, static-issuer token authenticates and
/// authorizes end-to-end through the UNMODIFIED API, proving the "no code change to accept the
/// token" claim. The token carries a <c>roles</c> array of <c>{tenant}:{fully-qualified-scope}</c>
/// and is validated against a second issuer configured for <c>TenantSource=CompoundRole</c> /
/// <c>ScopeClaims=["roles"]</c> — the shape <c>scripts/mint_token.py</c> emits.
///
/// This exercises the real ADR-015 <b>embedded-key</b> path: a public <c>jwks.json</c> is
/// written to a temp file and <c>Auth:Oidc:Issuers:1:JwksPath</c> points at it, so the
/// production <c>AuthExtensions.RegisterJwtBearer</c> embedded-key branch (not a test-only
/// PostConfigure) loads the keys with no HTTPS discovery fetch. The issuer is wired here rather
/// than in the shared <see cref="JwtApiFactory"/> so the factory's default Authentik-style
/// discovery issuer stays untouched.
/// </summary>
[Collection("Postgres")]
public class StaticIssuerCompoundRoleTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private JwtApiFactory _baseFactory = null!;
    private string _jwksPath = null!;

    public StaticIssuerCompoundRoleTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        // A real on-disk public JWKS for the embedded-key issuer — the production code reads it.
        _jwksPath = Path.Combine(Path.GetTempPath(), $"adr015-jwks-{Guid.NewGuid():N}.json");
        File.WriteAllText(_jwksPath, JwtTokenMinter.StaticJwksJson());

        _baseFactory = new JwtApiFactory(_postgres.ConnectionString);
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            // Register the static-LAN CompoundRole issuer as a second issuer (index 1),
            // embedded-key (ADR-015): JwksPath set, no Authority/discovery.
            builder.UseSetting("Auth:Oidc:Issuers:1:Name", JwtTokenMinter.CompoundRoleSchemeName);
            builder.UseSetting("Auth:Oidc:Issuers:1:Issuer", JwtTokenMinter.CompoundRoleIssuer);
            builder.UseSetting("Auth:Oidc:Issuers:1:Audience", JwtTokenMinter.TestAudience);
            builder.UseSetting("Auth:Oidc:Issuers:1:ScopeClaims:0", "roles");
            builder.UseSetting("Auth:Oidc:Issuers:1:TenantSource", "CompoundRole");
            builder.UseSetting("Auth:Oidc:Issuers:1:RoleSeparator", ":");
            builder.UseSetting("Auth:Oidc:Issuers:1:JwksPath", _jwksPath);

            // Mock embeddings collapse distinct entries; disable dedup so per-row audit
            // attribution is real (mirrors ActorClassAuditTests).
            builder.UseSetting("Deduplication:Enabled", "false");
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
        if (File.Exists(_jwksPath))
            File.Delete(_jwksPath);
    }

    private HttpClient CompoundRoleClient(string tenant, IEnumerable<string> scopes)
    {
        var token = JwtTokenMinter.MintCompoundRole(tenant, scopes);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient AdminClient()
    {
        // Cross-tenant admin via the default Authentik-style issuer (group → shared tenant).
        var token = JwtTokenMinter.Mint(tenant: "shared", scopes: [AuthConstants.AdminScope], groups: ["group-shared"]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object UniquePayload() => new
    {
        domain = "shared",
        title = $"ADR-015 static-issuer fixture {Guid.NewGuid():N}",
        body = $"Body for a compound-role authorization test ({Guid.NewGuid()}).",
        entryType = "Pattern",
        severity = "Info",
        source = "test"
    };

    [Fact]
    public async Task EmbeddedKeyToken_WithReadScope_AuthorizesListEndpoint()
    {
        // Read scope carried as "test:expertise.read" → ReadAccess policy satisfied, validated
        // against the embedded JWKS with no discovery fetch.
        var client = CompoundRoleClient("test", [AuthConstants.ReadScope]);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmbeddedKeyToken_WriteDraftAndAgent_CreatesInDerivedTenant_AndTagsAgent()
    {
        // roles: ["test:expertise.write.draft", "test:expertise.agent"].
        // Proves the full pipeline: embedded-key validation → roles-array extraction →
        // CompoundRole parse (tenant=test) → scope closure (write.draft ⊇ read) →
        // actor-class (agent scope) → tenant attribution.
        var client = CompoundRoleClient("test", [AuthConstants.WriteDraftScope, AuthConstants.AgentScope]);

        var create = await client.PostAsJsonAsync("/expertise", UniquePayload());
        create.StatusCode.Should().Be(HttpStatusCode.Created,
            await create.Content.ReadAsStringAsync());
        var entryId = (await create.Content.ReadJsonElementAsync()).GetProperty("id").GetGuid();

        var audit = await AdminClient().GetAsync("/audit");
        audit.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await audit.Content.ReadJsonElementAsync()).EnumerateArray().ToArray();
        var row = rows.Single(r => r.GetProperty("entryId").GetGuid() == entryId);

        row.GetProperty("tenant").GetString().Should().Be("test");
        row.GetProperty("actorClass").GetString().Should().Be("Agent");
        row.GetProperty("authMethod").GetString().Should().Be(AuthExtensions.BearerScheme);
    }

    [Fact]
    public async Task EmbeddedKeyToken_WithoutWriteScope_IsForbiddenOnCreate()
    {
        // Read-only token must NOT satisfy WriteAccess — authorization is enforced, not just parsed.
        var client = CompoundRoleClient("test", [AuthConstants.ReadScope]);

        var response = await client.PostAsJsonAsync("/expertise", UniquePayload());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
