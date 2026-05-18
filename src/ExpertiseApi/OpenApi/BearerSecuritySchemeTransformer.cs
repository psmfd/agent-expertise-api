using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ExpertiseApi.OpenApi;

/// <summary>
/// Document transformer that advertises the API's JWT Bearer authentication scheme
/// in the generated OpenAPI document. Without this, the spec lists secured operations
/// but provides no hint to clients (Scalar UI, codegen, LLM agents) about how to
/// authenticate.
///
/// Wired via <c>builder.Services.AddOpenApi(opts =&gt;
/// opts.AddDocumentTransformer&lt;BearerSecuritySchemeTransformer&gt;())</c> in Program.cs.
///
/// Targets Microsoft.OpenApi 2.x (the dependency shipped with
/// Microsoft.AspNetCore.OpenApi 10.0.7) — types live directly under
/// <c>Microsoft.OpenApi</c>, not the legacy <c>Microsoft.OpenApi.Models</c> namespace,
/// and security-scheme references use <see cref="OpenApiSecuritySchemeReference"/>.
///
/// Reference: <see href="https://learn.microsoft.com/aspnet/core/fundamentals/openapi/customize-openapi#use-document-transformers" />.
/// </summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private readonly IAuthenticationSchemeProvider _authenticationSchemeProvider;

    public BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider authenticationSchemeProvider)
    {
        _authenticationSchemeProvider = authenticationSchemeProvider;
    }

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var authSchemes = await _authenticationSchemeProvider.GetAllSchemesAsync();
        var hasBearer = authSchemes.Any(s =>
            string.Equals(s.Name, "Bearer", StringComparison.OrdinalIgnoreCase));

        if (!hasBearer)
            return;

        var bearerScheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT",
            Description = "JWT bearer token obtained from the configured OIDC issuer. " +
                          "Required scopes per operation are not yet advertised in the spec — " +
                          "see the README \"Auth scopes\" section for the read/write/admin matrix.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = bearerScheme;

        // Apply the requirement at the document root so every operation inherits it.
        // Operations that allow anonymous access (/health/*, /openapi/*) are filtered
        // out by ApiExplorer before this transformer runs.
        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>(),
        });
    }
}
