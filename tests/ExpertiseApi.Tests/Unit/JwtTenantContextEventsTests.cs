using ExpertiseApi.Auth;

namespace ExpertiseApi.Tests.Unit;

public class JwtTenantContextEventsTests
{
    [Fact]
    public void ExpandScopeClosure_NormalizesLegacyWriteToDraft()
    {
        var result = JwtTenantContextEvents.ExpandScopeClosure(new[] { AuthConstants.LegacyWriteScope });

        result.Should().Contain(AuthConstants.WriteDraftScope);
        result.Should().Contain(AuthConstants.ReadScope);
        result.Should().NotContain(AuthConstants.LegacyWriteScope);
        // Legacy alias must NOT escalate to approve or admin.
        result.Should().NotContain(AuthConstants.WriteApproveScope);
        result.Should().NotContain(AuthConstants.AdminScope);
    }

    [Fact]
    public void ExpandScopeClosure_AdminImpliesAll()
    {
        var result = JwtTenantContextEvents.ExpandScopeClosure(new[] { AuthConstants.AdminScope });

        result.Should().BeEquivalentTo(new[]
        {
            AuthConstants.AdminScope,
            AuthConstants.WriteApproveScope,
            AuthConstants.WriteDraftScope,
            AuthConstants.ReadScope
        });
    }

    [Fact]
    public void ExpandScopeClosure_ApproveImpliesDraftAndRead()
    {
        var result = JwtTenantContextEvents.ExpandScopeClosure(new[] { AuthConstants.WriteApproveScope });

        result.Should().Contain(AuthConstants.WriteDraftScope);
        result.Should().Contain(AuthConstants.ReadScope);
        result.Should().NotContain(AuthConstants.AdminScope);
    }

    [Fact]
    public void ExpandScopeClosure_DraftImpliesRead()
    {
        var result = JwtTenantContextEvents.ExpandScopeClosure(new[] { AuthConstants.WriteDraftScope });

        result.Should().Contain(AuthConstants.ReadScope);
        result.Should().NotContain(AuthConstants.WriteApproveScope);
    }

    [Fact]
    public void ExpandScopeClosure_ReadStaysRead()
    {
        var result = JwtTenantContextEvents.ExpandScopeClosure(new[] { AuthConstants.ReadScope });

        result.Should().BeEquivalentTo(new[] { AuthConstants.ReadScope });
    }

    [Fact]
    public void ParseCompoundRoles_SingleTenantSingleScope_ExtractsBoth()
    {
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            new[] { "team-alpha:expertise.read" }, separator: ":");

        tenant.Should().Be("team-alpha");
        scopes.Should().BeEquivalentTo(new[] { "expertise.read" });
    }

    [Fact]
    public void ParseCompoundRoles_SingleTenantMultipleScopes_AggregatesScopes()
    {
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            new[]
            {
                "team-alpha:expertise.read",
                "team-alpha:expertise.write.draft"
            },
            separator: ":");

        tenant.Should().Be("team-alpha");
        scopes.Should().BeEquivalentTo(new[] { "expertise.read", "expertise.write.draft" });
    }

    [Fact]
    public void ParseCompoundRoles_MultipleTenants_FirstWinsAndOthersDropped()
    {
        // Machine credentials are scoped to a single tenant; a token claiming two tenants
        // is rejected past the first. team-beta scopes must NOT leak through.
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            new[]
            {
                "team-alpha:expertise.read",
                "team-beta:expertise.admin"
            },
            separator: ":");

        tenant.Should().Be("team-alpha");
        scopes.Should().BeEquivalentTo(new[] { "expertise.read" });
        scopes.Should().NotContain("expertise.admin");
    }

    [Fact]
    public void ParseCompoundRoles_NoSeparator_YieldsNullTenantAndEmptyScopes()
    {
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            new[] { "expertise.read" }, separator: ":");

        tenant.Should().BeNull();
        scopes.Should().BeEmpty();
    }

    [Fact]
    public void ParseCompoundRoles_EmptyTenantOrScopeSegment_IsSkipped()
    {
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            new[]
            {
                ":expertise.read",        // empty tenant
                "team-alpha:",            // empty scope
                "team-alpha:expertise.read"
            },
            separator: ":");

        tenant.Should().Be("team-alpha");
        scopes.Should().BeEquivalentTo(new[] { "expertise.read" });
    }

    [Fact]
    public void ParseCompoundRoles_EmptyInput_YieldsNullTenant()
    {
        var (tenant, scopes) = JwtTenantContextEvents.ParseCompoundRoles(
            Array.Empty<string>(), separator: ":");

        tenant.Should().BeNull();
        scopes.Should().BeEmpty();
    }
}
