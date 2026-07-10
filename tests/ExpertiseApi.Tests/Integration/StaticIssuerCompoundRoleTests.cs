using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExpertiseApi.Auth;
using ExpertiseApi.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ExpertiseApi.Tests.Integration;

/// <summary>
/// ADR-014 regression guard: an offline-minted, static-issuer token authenticates and
/// authorizes end-to-end through the UNMODIFIED API, proving the "no code change" claim.
/// The token carries a <c>roles</c> array of <c>{tenant}:{fully-qualified-scope}</c> and is
/// validated against a second issuer configured for <c>TenantSource=CompoundRole</c> /
/// <c>ScopeClaims=["roles"]</c> — the shape <c>scripts/mint_token.py</c> emits.
///
/// The issuer is wired here (not in the shared <see cref="JwtApiFactory"/>) so the factory's
/// default Authentik-style issuer stays untouched; the second scheme's OIDC configuration is
/// injected in-process so no JWKS HTTP fetch occurs (the live HTTPS metadata fetch is a
/// framework concern, not an API-behaviour one).
/// </summary>
[Collection("Postgres")]
public class StaticIssuerCompoundRoleTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private JwtApiFactory _baseFactory = null!;

    public StaticIssuerCompoundRoleTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _baseFactory = new JwtApiFactory(_postgres.ConnectionString);
        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            // Register the static-LAN CompoundRole issuer as a second issuer (index 1).
            builder.UseSetting("Auth:Oidc:Issuers:1:Name", JwtTokenMinter.CompoundRoleSchemeName);
            builder.UseSetting("Auth:Oidc:Issuers:1:Issuer", JwtTokenMinter.CompoundRoleIssuer);
            builder.UseSetting("Auth:Oidc:Issuers:1:Audience", JwtTokenMinter.TestAudience);
            builder.UseSetting("Auth:Oidc:Issuers:1:ScopeClaims:0", "roles");
            builder.UseSetting("Auth:Oidc:Issuers:1:TenantSource", "CompoundRole");
            builder.UseSetting("Auth:Oidc:Issuers:1:RoleSeparator", ":");

            // Mock embeddings collapse distinct entries; disable dedup so per-row audit
            // attribution is real (mirrors ActorClassAuditTests).
            builder.UseSetting("Deduplication:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                // Preload OIDC config for the second scheme so JwtBearer skips the JWKS fetch.
                services.PostConfigure<JwtBearerOptions>(JwtTokenMinter.CompoundRoleSchemeName, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration
                    {
                        Issuer = JwtTokenMinter.CompoundRoleIssuer
                    };
                    options.Configuration.SigningKeys.Add(JwtTokenMinter.SigningKey);
                    options.TokenValidationParameters.IssuerSigningKey = JwtTokenMinter.SigningKey;
                    options.MetadataAddress = null!;
                    options.ConfigurationManager = null;
                });
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _baseFactory.DisposeAsync();
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
        title = $"ADR-014 static-issuer fixture {Guid.NewGuid():N}",
        body = $"Body for a compound-role authorization test ({Guid.NewGuid()}).",
        entryType = "Pattern",
        severity = "Info",
        source = "test"
    };

    [Fact]
    public async Task CompoundRoleToken_WithReadScope_AuthorizesListEndpoint()
    {
        // Read scope carried as "test:expertise.read" → ReadAccess policy satisfied.
        var client = CompoundRoleClient("test", [AuthConstants.ReadScope]);

        var response = await client.GetAsync("/expertise");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CompoundRoleToken_WriteDraftAndAgent_CreatesInDerivedTenant_AndTagsAgent()
    {
        // roles: ["test:expertise.write.draft", "test:expertise.agent"].
        // Proves the full pipeline: roles-array extraction → CompoundRole parse (tenant=test)
        // → scope closure (write.draft ⊇ read) → actor-class (agent scope) → tenant attribution.
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
    public async Task CompoundRoleToken_WithoutWriteScope_IsForbiddenOnCreate()
    {
        // Read-only token must NOT satisfy WriteAccess — authorization is enforced, not just parsed.
        var client = CompoundRoleClient("test", [AuthConstants.ReadScope]);

        var response = await client.PostAsJsonAsync("/expertise", UniquePayload());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
