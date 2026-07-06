using System.Text.Json.Serialization;

namespace ExpertiseApi.Services.Sync;

/// <summary>
/// Outbound wire shape for one entry POSTed to the hub's <c>/expertise/batch</c>.
/// Deliberately a standalone DTO (not the server's <c>CreateExpertiseRequest</c>):
/// enums travel as their string names and serialization is source-generated, so the
/// worker's wire format cannot drift with server-side serializer configuration.
/// <c>Tenant</c> is intentionally absent — the hub assigns the spoke's tenant from the
/// authenticated token (ADR-003/ADR-013), and a <c>write.draft</c>-scoped spoke could
/// not use the <c>"shared"</c> override anyway.
/// </summary>
internal sealed record SyncBatchItem
{
    public required string Domain { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string EntryType { get; init; }
    public required string Severity { get; init; }
    public required string Source { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? SourceVersion { get; init; }

    /// <summary>Origin-side author, informational reviewer context on the hub (ADR-013).</summary>
    public string? OriginAuthorPrincipal { get; init; }
}

/// <summary>Per-item result mirrored from the batch endpoint's <c>BatchEntryResult</c>.</summary>
internal sealed record SyncBatchResult
{
    public int Index { get; init; }
    public required string Status { get; init; }
    public Guid? Id { get; init; }
    public string? Error { get; init; }
}

/// <summary>OAuth2 client_credentials token response (RFC 6749 §5.1 subset).</summary>
internal sealed record HubTokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SyncBatchItem>))]
[JsonSerializable(typeof(List<SyncBatchResult>))]
[JsonSerializable(typeof(HubTokenResponse))]
internal sealed partial class SyncJsonContext : JsonSerializerContext;
