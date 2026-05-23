namespace ExpertiseApi.Auth;

internal class OidcIssuerOptions
{
    /// <summary>
    /// Logical name for the issuer (e.g. "Entra", "Authentik"). Used as the JwtBearer scheme name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Exact issuer URL as it appears in the discovery document — must byte-match the <c>iss</c>
    /// claim. Authentik includes a trailing slash; Entra does not. Do not normalize.
    /// </summary>
    public required string Issuer { get; set; }

    /// <summary>Primary audience.</summary>
    public required string Audience { get; set; }

    /// <summary>
    /// Additional accepted audiences (e.g. v1 App ID URI form alongside v2 GUID for Entra).
    /// </summary>
    public List<string> AdditionalAudiences { get; set; } = [];

    /// <summary>
    /// Claim names to read scope strings from. Microsoft Entra emits <c>scp</c> for delegated
    /// flows and <c>roles</c> for client_credentials; both should be listed for that issuer.
    /// Authentik (and RFC 9068 issuers) emit <c>scope</c>.
    /// </summary>
    public List<string> ScopeClaims { get; set; } = ["scope"];

    /// <summary>
    /// How to derive the tenant from the token.
    /// </summary>
    public TenantSource TenantSource { get; set; } = TenantSource.Groups;

    /// <summary>
    /// Separator for compound role names (<c>"team-alpha:expertise.read"</c>) when
    /// <see cref="TenantSource"/> is <see cref="TenantSource.CompoundRole"/>.
    /// </summary>
    public string RoleSeparator { get; set; } = ":";

    /// <summary>
    /// Group claim → tenant slug mapping. Used when <see cref="TenantSource"/> is
    /// <see cref="TenantSource.Groups"/>. Keys are group object IDs (Entra) or group slugs
    /// (Authentik).
    /// </summary>
    public Dictionary<string, string> GroupToTenantMapping { get; set; } = [];

    /// <summary>
    /// Group claim name (defaults to <c>"groups"</c>).
    /// </summary>
    public string GroupClaim { get; set; } = "groups";
}

internal enum TenantSource
{
    /// <summary>Walk group claims through <see cref="OidcIssuerOptions.GroupToTenantMapping"/>.</summary>
    Groups,

    /// <summary>
    /// Parse each scope-claim entry as <c>{tenant}{separator}{scope}</c>. Used for Entra
    /// <c>client_credentials</c> tokens which do not emit groups for service principals.
    /// </summary>
    CompoundRole
}
