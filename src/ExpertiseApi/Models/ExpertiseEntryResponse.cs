using System.Text.Json.Serialization;
using ExpertiseApi.Hygiene;

namespace ExpertiseApi.Models;

/// <summary>
/// Hygienized response envelope for <see cref="ExpertiseEntry"/>. Part D C7 / ADR-008:
/// free-text fields (<c>Title</c>, <c>Body</c>, <c>RejectionReason</c>) are emitted as
/// <see cref="HygienizedField"/> sub-objects so consumers can reason about content
/// trust class and the hygiene transforms that were applied; the response-level
/// <c>_hygiene</c> block carries the per-response nonce and detector manifest.
/// <para>
/// Mapped from the EF entity via <see cref="From"/>. Trusted-structured fields (enums,
/// IDs, timestamps, server-derived strings) are emitted as primitives and are
/// <strong>not</strong> wrapped \u2014 wrapping a server-controlled value would imply it
/// was untrusted, which would be misleading.
/// </para>
/// <para>
/// Explicitly omits <c>Embedding</c> (Pgvector) and <c>SearchVector</c> (NpgsqlTsVector)
/// so the new DTO surface cannot accidentally regress to exposing them.
/// </para>
/// </summary>
internal sealed record ExpertiseEntryResponse(
    Guid Id,
    string Domain,
    List<string> Tags,
    HygienizedField Title,
    HygienizedField Body,
    EntryType EntryType,
    Severity Severity,
    string Source,
    string? SourceVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeprecatedAt,
    string Tenant,
    Visibility Visibility,
    string AuthorPrincipal,
    string? AuthorAgent,
    string? OriginInstanceId,
    HygienizedField? OriginAuthorPrincipal,
    string? IntegrityHash,
    ReviewState ReviewState,
    string? ReviewedBy,
    DateTime? ReviewedAt,
    HygienizedField? RejectionReason,
    [property: JsonPropertyName("_hygiene")] HygieneEnvelope Hygiene)
{
    /// <summary>
    /// Hygienize a single entry. Caller is responsible for providing a fresh nonce \u2014
    /// for a list response, mint <strong>one</strong> nonce and share it across every
    /// item so consumers can parse the delimiter pair from any <c>_hygiene</c> block.
    /// </summary>
    public static ExpertiseEntryResponse From(ExpertiseEntry entry, IResponseHygiene hygiene, string nonce)
    {
        var manifest = hygiene.GetManifest(nonce);
        return new ExpertiseEntryResponse(
            Id: entry.Id,
            Domain: entry.Domain,
            Tags: entry.Tags,
            Title: hygiene.Hygienize(entry.Title, ContentClass.UserSuppliedFreeText, nonce),
            Body: hygiene.Hygienize(entry.Body, ContentClass.UserSuppliedFreeText, nonce),
            EntryType: entry.EntryType,
            Severity: entry.Severity,
            Source: entry.Source,
            SourceVersion: entry.SourceVersion,
            CreatedAt: entry.CreatedAt,
            UpdatedAt: entry.UpdatedAt,
            DeprecatedAt: entry.DeprecatedAt,
            Tenant: entry.Tenant,
            Visibility: entry.Visibility,
            AuthorPrincipal: entry.AuthorPrincipal,
            AuthorAgent: entry.AuthorAgent,
            // ADR-013 origin attribution. OriginInstanceId is SERVER-derived (trusted
            // structured — wrapping would misleadingly imply it was untrusted);
            // OriginAuthorPrincipal arrived in a request body from another instance,
            // so it gets the full user-supplied hygiene pipeline.
            OriginInstanceId: entry.OriginInstanceId,
            OriginAuthorPrincipal: entry.OriginAuthorPrincipal is null
                ? null
                : hygiene.Hygienize(entry.OriginAuthorPrincipal, ContentClass.UserSuppliedFreeText, nonce),
            // IntegrityHash is computed over the ORIGINAL entity content (write-time);
            // hygienizing the displayed Title/Body does NOT invalidate the audit chain
            // because the audit row records the hash of the canonical entity, not the
            // response shape. Preserved as-is.
            IntegrityHash: entry.IntegrityHash,
            ReviewState: entry.ReviewState,
            ReviewedBy: entry.ReviewedBy,
            ReviewedAt: entry.ReviewedAt,
            RejectionReason: entry.RejectionReason is null
                ? null
                : hygiene.Hygienize(entry.RejectionReason, ContentClass.ReviewerAuthoredFreeText, nonce),
            Hygiene: new HygieneEnvelope(
                Version: manifest.Version,
                Nonce: manifest.Nonce,
                DelimiterOpen: manifest.DelimiterOpen,
                DelimiterClose: manifest.DelimiterClose,
                Detectors: manifest.Detectors,
                Disclaimer: manifest.Disclaimer));
    }

    /// <summary>Convenience overload for endpoints that mint their own nonce per response.</summary>
    public static ExpertiseEntryResponse From(ExpertiseEntry entry, IResponseHygiene hygiene)
        => From(entry, hygiene, hygiene.MintNonce());

    /// <summary>
    /// Hygienize a list of entries under a single shared nonce. Consumers parse the
    /// delimiter pair from any item's <c>_hygiene</c> block.
    /// </summary>
    public static List<ExpertiseEntryResponse> FromMany(IEnumerable<ExpertiseEntry> entries, IResponseHygiene hygiene)
    {
        var nonce = hygiene.MintNonce();
        return entries.Select(e => From(e, hygiene, nonce)).ToList();
    }
}
