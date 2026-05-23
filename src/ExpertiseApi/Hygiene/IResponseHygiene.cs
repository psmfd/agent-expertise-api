namespace ExpertiseApi.Hygiene;

/// <summary>
/// Content-class taxonomy for the response-hygiene envelope (Part D C7 / ADR-008).
/// Determines which transforms apply and how downstream consumers should reason about
/// the field's trust level.
/// </summary>
internal enum ContentClass
{
    /// <summary>
    /// Server-validated structured values (enums, IDs, timestamps, server-derived
    /// claims). No hygiene applied; returned as primitives.
    /// </summary>
    TrustedStructured = 0,

    /// <summary>
    /// Reviewer-authored free text (e.g. <c>RejectionReason</c>). Full PII strip and
    /// delimiter-wrap; instruction-heuristic runs in <strong>report-only</strong> mode
    /// because reviewers may legitimately quote attacker prose verbatim when explaining
    /// a rejection. Counts are surfaced; spans are not wrapped.
    /// </summary>
    ReviewerAuthoredFreeText = 1,

    /// <summary>
    /// User-supplied free text (e.g. <c>Title</c>, <c>Body</c>). Full PII strip,
    /// injection-heuristic wrapping, and delimiter-wrap with per-response nonce.
    /// </summary>
    UserSuppliedFreeText = 2,
}

/// <summary>
/// Hygienizes a free-text field into the response envelope shape:
/// <c>{ contentClass, value, hygieneApplied[] }</c>. Used by DTO mappers (e.g.
/// <c>ExpertiseEntryResponse.From</c>) to convert raw entity strings into the C7
/// response envelope.
/// </summary>
internal interface IResponseHygiene
{
    /// <summary>
    /// Mint a fresh per-response nonce. Must be called <strong>once per HTTP response</strong>;
    /// the nonce is shared across every wrapped field in that response and surfaced in the
    /// envelope's <c>_hygiene.nonce</c> so consumers can parse the delimiter pair.
    /// </summary>
    string MintNonce();

    /// <summary>
    /// Hygienize a single free-text field. Returns the wrapped value, the content class,
    /// and the ordered list of transforms applied (for the envelope's <c>hygieneApplied</c>
    /// array). <paramref name="nonce"/> must be the value returned by <see cref="MintNonce"/>.
    /// </summary>
    HygienizedField Hygienize(string? input, ContentClass contentClass, string nonce);

    /// <summary>
    /// Manifest fields for the response-level <c>_hygiene</c> envelope. Carries the
    /// detector version, the detector class list, and the disclaimer text so consumers
    /// can reason about coverage.
    /// </summary>
    HygieneManifest GetManifest(string nonce);
}

/// <summary>
/// Per-field envelope returned by <see cref="IResponseHygiene.Hygienize"/>. Serialized
/// in place of a bare string on <c>ExpertiseEntryResponse</c>.
/// </summary>
internal sealed record HygienizedField(
    string ContentClass,
    string? Value,
    IReadOnlyList<string> HygieneApplied);

/// <summary>Response-level <c>_hygiene</c> manifest block.</summary>
internal sealed record HygieneManifest(
    string Version,
    string Nonce,
    string DelimiterOpen,
    string DelimiterClose,
    IReadOnlyList<string> Detectors,
    string Disclaimer);
