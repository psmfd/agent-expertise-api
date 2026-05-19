using System.Buffers;

namespace ExpertiseApi.Services.Idempotency;

/// <summary>
/// Validates the caller-supplied <c>Idempotency-Key</c> header value against the
/// shape mandated by IETF <c>draft-ietf-httpapi-idempotency-key-header-06</c>
/// §2.2: 1–255 ASCII printable octets, no whitespace, no control characters.
/// <para>
/// Rejection is reported via <see cref="ValidationResult"/> so the filter can
/// surface a structured 400 ProblemDetails with the specific failure reason
/// (length / charset / whitespace) instead of an opaque "bad key" message.
/// </para>
/// </summary>
internal static class IdempotencyKeyValidator
{
    /// <summary>Minimum legal length (inclusive).</summary>
    public const int MinLength = 1;

    /// <summary>Maximum legal length (inclusive).</summary>
    public const int MaxLength = 255;

    /// <summary>Outcome record.</summary>
    internal readonly record struct ValidationResult(bool IsValid, string? Reason);

    private static readonly SearchValues<char> ForbiddenWhitespace =
        SearchValues.Create(" \t\r\n\v\f");

    /// <summary>
    /// Returns a validation result for <paramref name="key"/>. Empty / null /
    /// over-length / non-printable-ASCII / whitespace-bearing inputs all return
    /// <c>IsValid=false</c> with a one-line operator-readable reason. The reason
    /// is suitable for inclusion in a ProblemDetails <c>detail</c> field and
    /// does not echo the key value (avoids reflection-amplified XSS in any
    /// downstream log viewer).
    /// </summary>
    public static ValidationResult Validate(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return new(false, "Idempotency-Key must be present and non-empty.");

        if (key.Length < MinLength || key.Length > MaxLength)
            return new(false, $"Idempotency-Key length must be between {MinLength} and {MaxLength} characters.");

        if (key.AsSpan().IndexOfAny(ForbiddenWhitespace) >= 0)
            return new(false, "Idempotency-Key must not contain whitespace.");

        // IETF §2.2 charset: VCHAR (printable ASCII 0x21–0x7E). Reject anything
        // outside that range — control chars, DEL (0x7F), and non-ASCII alike.
        // The loop is hot-path; SearchValues<char>.Create over the full
        // disallowed range would inflate the table footprint without
        // measurable speedup at length ≤ 255.
        foreach (var c in key)
        {
            if (c < 0x21 || c > 0x7E)
                return new(false, "Idempotency-Key must contain only ASCII printable characters (0x21–0x7E).");
        }

        return new(true, null);
    }
}
