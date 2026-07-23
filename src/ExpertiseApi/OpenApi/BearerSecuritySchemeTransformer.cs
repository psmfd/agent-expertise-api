using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ExpertiseApi.OpenApi;

/// <summary>
/// Document transformer that advertises the API's JWT Bearer authentication scheme
/// in the generated OpenAPI document and suppresses the inherited document-level
/// security requirement on any endpoint marked <c>.AllowAnonymous()</c>.
///
/// Without this transformer the spec lists secured operations but provides no hint
/// to clients (Scalar UI, codegen, LLM agents) about how to authenticate. Without
/// the per-operation override, any future <c>MapGet("/foo", ...).AllowAnonymous()</c>
/// would silently inherit the document-level Bearer requirement and incorrectly
/// advertise auth-required to clients and audit tooling. (The endpoints that exist
/// today &#8212; <c>/openapi/*</c>, <c>/health/*</c>, <c>/metrics</c> &#8212; are absent from
/// the document entirely because <c>MapOpenApi</c>, <c>MapHealthChecks</c> and the
/// prometheus-net middleware do not register ApiExplorer descriptors. The override
/// keeps the transformer correct for routes that DO register descriptors, future
/// or otherwise.)
///
/// Wired via <c>builder.Services.AddOpenApi(opts =&gt;
/// opts.AddDocumentTransformer&lt;BearerSecuritySchemeTransformer&gt;())</c> in Program.cs.
///
/// Targets Microsoft.OpenApi 2.x (the dependency shipped with
/// Microsoft.AspNetCore.OpenApi 10.0.7) &#8212; types live directly under
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
                          "In production (Auth:Mode=Oidc, enforced by EnforceModeGuard) this is the only " +
                          "accepted credential. In Development Auth:Mode=Hybrid additionally accepts a " +
                          "static API key or a `dev:{tenant}:{scope}` LocalDev token, both supplied via " +
                          "`Authorization: Bearer <value>` \u2014 every credential type uses the Authorization " +
                          "header; there is no `X-Api-Key` header (#331). Required scopes per operation are " +
                          "not yet in the spec; see the README \"Auth scopes\" section for the " +
                          "read/write/admin matrix.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = bearerScheme;

        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = new List<string>(),
        });

        // Per-operation override: any endpoint that carries IAllowAnonymous metadata
        // (typically from .AllowAnonymous()) gets an empty `security: []` so the
        // document-level Bearer requirement does not apply. Without this, a future
        // .MapGet("/foo", ...).AllowAnonymous() (without .ExcludeFromDescription())
        // would inherit Bearer-required in the spec and mislead clients / break
        // codegen.
        foreach (var group in context.DescriptionGroups)
        {
            foreach (var apiDesc in group.Items)
            {
                var endpointMetadata = apiDesc.ActionDescriptor.EndpointMetadata;
                var isAnonymous = endpointMetadata.OfType<IAllowAnonymous>().Any()
                    && !endpointMetadata.OfType<IAuthorizeData>().Any();
                if (!isAnonymous)
                    continue;

                if (document.Paths is null)
                    continue;

                var path = "/" + (apiDesc.RelativePath?.TrimStart('/') ?? string.Empty);
                if (!document.Paths.TryGetValue(path, out var pathItem))
                    continue;

                var verb = apiDesc.HttpMethod;
                if (string.IsNullOrEmpty(verb))
                    continue;
                var method = HttpMethod.Parse(verb);
                if (pathItem.Operations is null || !pathItem.Operations.TryGetValue(method, out var operation))
                    continue;

                // Empty list (not null) = "no security applies to this operation".
                // null would inherit the document-level requirement.
                operation.Security = new List<OpenApiSecurityRequirement>();
            }
        }
    }
}
