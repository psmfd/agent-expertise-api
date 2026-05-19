using ExpertiseApi.Endpoints.Filters;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace ExpertiseApi.OpenApi;

/// <summary>
/// Document transformer that advertises the <c>Idempotency-Key</c> header on
/// every operation whose endpoint carries <see cref="RequireIdempotencyMetadata"/>
/// (i.e. routes attached via <c>.RequireIdempotency()</c>). Header is described
/// as optional today (matching the soft-require posture of
/// <c>Idempotency:RequireKey=false</c>); when the operator flips to hard-require
/// in environment overlay, update the <c>Required=true</c> below in the same
/// PR per the per-PR doc-sync rule.
/// <para>
/// Also documents the response-side <c>Idempotency-Replay: true</c> indicator
/// in the description so client codegen / agents can branch on cached vs
/// fresh responses.
/// </para>
/// </summary>
internal sealed class IdempotencyKeyDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Paths is null)
            return Task.CompletedTask;

        foreach (var group in context.DescriptionGroups)
        {
            foreach (var apiDesc in group.Items)
            {
                var endpointMetadata = apiDesc.ActionDescriptor.EndpointMetadata;
                if (endpointMetadata.OfType<RequireIdempotencyMetadata>().FirstOrDefault() is null)
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

                operation.Parameters ??= new List<IOpenApiParameter>();

                // Skip if a transformer earlier in the pipeline already added it
                // (defensive — today no other transformer touches headers, but
                // hand-rolled additions in future shouldn't double-register).
                var alreadyPresent = operation.Parameters.Any(p =>
                    p is OpenApiParameter op
                    && string.Equals(op.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                    && op.In == ParameterLocation.Header);
                if (alreadyPresent)
                    continue;

                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "Idempotency-Key",
                    In = ParameterLocation.Header,
                    Required = false,
                    Description =
                        "Caller-supplied idempotency key per IETF draft-ietf-httpapi-idempotency-key-header-06. " +
                        "1–255 ASCII printable characters, no whitespace. When supplied, a replay of the same " +
                        "key + body within 24h returns the original response unchanged and the response carries " +
                        "`Idempotency-Replay: true`. Same key with a different body returns 409. Optional today " +
                        "(Idempotency:RequireKey=false); operators may flip to hard-require after consumers ship " +
                        "header generation (see issues #205, #206).",
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String,
                        MinLength = 1,
                        MaxLength = 255,
                        Pattern = "^[\\x21-\\x7E]+$",
                    },
                });
            }
        }

        return Task.CompletedTask;
    }
}
