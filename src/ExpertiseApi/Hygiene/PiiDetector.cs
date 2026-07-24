using System.Text.RegularExpressions;

namespace ExpertiseApi.Hygiene;

/// <summary>
/// Regex-based PII redaction for free-text response fields. Each detector class is
/// compiled once at construction. <strong>Every</strong> pattern uses
/// <see cref="RegexOptions.NonBacktracking"/> for a linear-time guarantee against ReDoS
/// (.NET 7+); each also carries a per-match timeout as belt-and-suspenders, and the
/// timeout path fails <strong>closed</strong> (see <see cref="ApplyOne"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Detector classes (v1.0):</strong> email, phone, AWS access key, AWS secret
/// (contextual), GitHub PAT, JWT, credentials-in-URL, PEM private-key header, IPv4/IPv6
/// address. The class list is part of the C7 response envelope's <c>_hygiene.detectors</c>
/// manifest so consumers can reason about coverage; bumping the list requires bumping
/// <see cref="DetectorVersion"/>.
/// </para>
/// <para>
/// <strong>Replacement strategy:</strong> matches are replaced with the literal placeholder
/// <c>[REDACTED:&lt;class&gt;]</c>. The class name lets a downstream LLM reason about what
/// was removed without exposing the value. Match counts are tracked per class so the
/// orchestrator can populate the <c>hygieneApplied</c> array on the per-field envelope.
/// </para>
/// <para>
/// <strong>Known false-positive guardrails:</strong> the phone pattern is strict E.164
/// (requires a leading <c>+</c>); the AWS secret pattern requires a context word
/// (<c>aws_secret</c> / <c>secret</c> / <c>AWS_SECRET_ACCESS_KEY</c>) within 32 chars
/// of the candidate 40-char base64-ish run to avoid matching arbitrary high-entropy
/// strings. The injection-heuristic pipeline still wraps suspect spans so false
/// negatives at the PII layer are partially recoverable.
/// </para>
/// </remarks>
internal static class PiiDetector
{
    public const string DetectorVersion = "1.0";

    public static readonly IReadOnlyList<string> KnownClasses =
    [
        "email", "phone", "aws-access-key", "aws-secret", "github-pat", "jwt",
        "url-credentials", "private-key-header", "ip-address"
    ];

    // RegexOptions.NonBacktracking guarantees linear-time matching for the supported
    // subset (no backreferences, no lookaround). ALL detectors are NonBacktracking as
    // of #333 Finding 2 — the AwsSecret lookbehind that previously forced a
    // backtracking-capable option was rewritten to capture the secret in a group
    // instead (see below), removing the only pattern that could ReDoS. There is no
    // longer a lookaround/backtracking option constant: a future lookaround pattern
    // must reintroduce one deliberately, and the fail-closed timeout path in ApplyOne
    // remains the safety net for any pattern that ever times out.
    private const RegexOptions NbOpts = RegexOptions.Compiled | RegexOptions.NonBacktracking
                                       | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(50);

    // Email: RFC-loose, anchored to word boundaries. Linear under NonBacktracking.
    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", NbOpts, FastTimeout);

    // Phone (strict E.164, requires a leading + per ITU). Without a + prefix we don't
    // attempt detection — false positives on hex nonces and numeric identifiers were
    // worse than the false negatives on bare 10-digit US numbers in our content.
    private static readonly Regex Phone = new(
        @"\+[1-9]\d{0,2}[\s.\-]?\(?\d{2,4}\)?[\s.\-]?\d{3,4}[\s.\-]?\d{3,4}",
        NbOpts, FastTimeout);

    private static readonly Regex AwsAccessKey = new(
        @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", NbOpts, FastTimeout);

    // AWS secret: requires a context word within 32 chars before the candidate (40-char
    // base64-ish run). Rewritten for #333 Finding 2 to be NonBacktracking-safe: the
    // former lookbehind (context) + lookahead (trailing boundary) forced a
    // backtracking-capable option and a ReDoS-vulnerable 100ms timeout. Now the context
    // word and separator are matched inline (consumed, not looked-behind) and the secret
    // is pulled into capture group 1; ApplyOne redacts ONLY group 1, so the emitted text
    // is byte-identical to the old lookbehind form ("aws_secret_access_key=<40>" ->
    // "aws_secret_access_key=[REDACTED:aws-secret]"). The trailing boundary is consumed
    // via (?:[^...]|$) — a lookahead is not permitted under NonBacktracking — and, being
    // outside group 1, is re-emitted verbatim. Bounded {0,32}/{40} quantifiers keep it
    // linear; it can no longer time out on adversarial padding.
    private static readonly Regex AwsSecret = new(
        @"(?:aws[_-]?secret(?:[_-]?access[_-]?key)?|secret(?:[_-]?key)?)[^A-Za-z0-9/+]{0,32}([A-Za-z0-9/+=]{40})(?:[^A-Za-z0-9/+=]|$)",
        NbOpts, FastTimeout);

    private static readonly Regex GithubPat = new(
        @"\b(?:ghp|gho|ghs|ghr|ghu)_[A-Za-z0-9]{36}\b", NbOpts, FastTimeout);

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        NbOpts, FastTimeout);

    private static readonly Regex UrlCredentials = new(
        @"\b(?:https?|ftp)://[^\s/:@]+:[^\s/@]+@\S+", NbOpts, FastTimeout);

    private static readonly Regex PrivateKeyHeader = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH |PGP |DSA )?PRIVATE KEY-----", NbOpts, FastTimeout);

    // IPv4 dotted-quad and IPv6 colon-hex. Per GDPR / CJEU Breyer C-582/14, IP is PII.
    // Structurally bounded by character classes — NonBacktracking is safe here.
    private static readonly Regex IpAddress = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\b" +
        @"|\b(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}\b",
        NbOpts, FastTimeout);

    // Detector run order. URL credentials FIRST so the user:pass@host segment is
    // replaced before the email pattern would otherwise consume the @host tail.
    private static readonly (Regex Pattern, string Class)[] Detectors =
    [
        (UrlCredentials, "url-credentials"),
        (Email, "email"),
        (AwsAccessKey, "aws-access-key"),
        (AwsSecret, "aws-secret"),
        (GithubPat, "github-pat"),
        (Jwt, "jwt"),
        (PrivateKeyHeader, "private-key-header"),
        (Phone, "phone"),
        (IpAddress, "ip-address"),
    ];

    /// <summary>
    /// Apply all detectors to <paramref name="input"/>. Returns the redacted text and
    /// per-class match counts (zero-count classes omitted). On a regex timeout the field
    /// is fully suppressed (fail-closed) and the remaining detectors are skipped.
    /// </summary>
    public static PiiResult Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new PiiResult(input, EmptyCounts);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = input;

        foreach (var (pattern, className) in Detectors)
        {
            current = ApplyOne(current, pattern, className, counts, out var timedOut);
            if (timedOut)
                // Fail-closed: the field is already replaced with a suppression
                // sentinel. Running the remaining detectors against that sentinel is
                // moot, so short-circuit \u2014 nothing sensitive can survive a fully
                // suppressed field.
                break;
        }

        return new PiiResult(current, counts);
    }

    /// <summary>
    /// Apply one detector. When the pattern defines capture group 1, only that span is
    /// redacted and the surrounding match text is preserved verbatim (used by AwsSecret,
    /// whose match includes a context word that must survive); otherwise the whole match
    /// is replaced. On <see cref="RegexMatchTimeoutException"/> the method fails CLOSED
    /// (#333 Finding 2): it returns a whole-field suppression sentinel and sets
    /// <paramref name="timedOut"/>, because a timeout yields no match positions and
    /// returning the original would ship an unredacted secret to an LLM consumer.
    /// </summary>
    internal static string ApplyOne(
        string input, Regex pattern, string className, Dictionary<string, int> counts, out bool timedOut)
    {
        var matchCount = 0;
        try
        {
            var replaced = pattern.Replace(input, m =>
            {
                matchCount++;
                if (m.Groups.Count > 1 && m.Groups[1].Success)
                {
                    var g = m.Groups[1];
                    var rel = g.Index - m.Index;
                    return $"{m.Value[..rel]}[REDACTED:{className}]{m.Value[(rel + g.Length)..]}";
                }
                return $"[REDACTED:{className}]";
            });
            if (matchCount > 0)
                counts[className] = matchCount;
            timedOut = false;
            return replaced;
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail CLOSED: a timeout carries no match boundaries, so selective redaction
            // is impossible. Suppress the WHOLE field rather than return the original
            // (the pre-#333 behaviour, which shipped the unredacted value). Consumers
            // reading a "{class}-timeout" key in hygieneApplied MUST treat the field as
            // fully suppressed, not partially redacted. The NonBacktracking rewrite of
            // every detector makes this path unreachable on today's patterns; it remains
            // the safety net for any future pattern or pathological input.
            counts[$"{className}-timeout"] = 1;
            timedOut = true;
            return $"[REDACTED:{className}-timeout]";
        }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>Result of <see cref="PiiDetector.Redact"/>.</summary>
internal sealed record PiiResult(string Text, IReadOnlyDictionary<string, int> Counts);
