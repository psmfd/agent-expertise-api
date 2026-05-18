using System.Text.RegularExpressions;

namespace ExpertiseApi.Hygiene;

/// <summary>
/// Regex-based PII redaction for free-text response fields. Each detector class is
/// compiled once at construction. Patterns use <see cref="RegexOptions.NonBacktracking"/>
/// for linear-time guarantee against ReDoS (.NET 7+); each pattern carries a per-match
/// timeout as belt-and-suspenders.
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
    // subset (no backreferences, no lookaround). Patterns that need lookaround are
    // called out below and use a tight timeout as the fallback.
    private const RegexOptions NbOpts = RegexOptions.Compiled | RegexOptions.NonBacktracking
                                       | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions LookaroundOpts = RegexOptions.Compiled | RegexOptions.IgnoreCase
                                              | RegexOptions.CultureInvariant;
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SlowTimeout = TimeSpan.FromMilliseconds(100);

    // Email: RFC-loose, anchored to word boundaries. Linear under NonBacktracking.
    private static readonly Regex Email = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", NbOpts, FastTimeout);

    // Phone (E.164-ish). Lookaround used to skip dotted version strings like "1.10.0",
    // so this falls back to backtracking with a tight timeout.
    private static readonly Regex Phone = new(
        @"(?<![\d.])\+?[1-9]\d{1,2}[\s.\-]?\(?\d{2,4}\)?[\s.\-]?\d{3,4}[\s.\-]?\d{3,4}(?![\d.])",
        LookaroundOpts, FastTimeout);

    private static readonly Regex AwsAccessKey = new(
        @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", NbOpts, FastTimeout);

    // AWS secret: requires a context word within 64 chars before the candidate (40-char
    // base64-ish run). Lookaround for context proximity; tight timeout.
    private static readonly Regex AwsSecret = new(
        @"(?<=(?:aws[_-]?secret|AWS[_-]?SECRET[_-]?ACCESS[_-]?KEY|secret(?:[_-]?key)?)[^A-Za-z0-9/+=]{0,32})[A-Za-z0-9/+=]{40}(?![A-Za-z0-9/+=])",
        LookaroundOpts, SlowTimeout);

    private static readonly Regex GithubPat = new(
        @"\b(?:ghp|gho|ghs|ghr|ghu)_[A-Za-z0-9]{36}\b", NbOpts, FastTimeout);

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        NbOpts, FastTimeout);

    private static readonly Regex UrlCredentials = new(
        @"\b(?:https?|ftp)://[^\s/:@]+:[^\s/@]+@\S+", NbOpts, SlowTimeout);

    private static readonly Regex PrivateKeyHeader = new(
        @"-----BEGIN (?:RSA |EC |OPENSSH |PGP |DSA )?PRIVATE KEY-----", NbOpts, FastTimeout);

    // IPv4 dotted-quad and IPv6 colon-hex. Per GDPR / CJEU Breyer C-582/14, IP is PII.
    // Structurally bounded by character classes — NonBacktracking is safe here.
    private static readonly Regex IpAddress = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\b" +
        @"|\b(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}\b",
        NbOpts, FastTimeout);

    /// <summary>
    /// Apply all detectors to <paramref name="input"/>. Returns the redacted text and
    /// per-class match counts (zero-count classes omitted).
    /// </summary>
    public static PiiResult Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new PiiResult(input, EmptyCounts);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var current = input;

        current = ApplyOne(current, Email, "email", counts);
        current = ApplyOne(current, AwsAccessKey, "aws-access-key", counts);
        current = ApplyOne(current, AwsSecret, "aws-secret", counts);
        current = ApplyOne(current, GithubPat, "github-pat", counts);
        current = ApplyOne(current, Jwt, "jwt", counts);
        current = ApplyOne(current, PrivateKeyHeader, "private-key-header", counts);
        current = ApplyOne(current, UrlCredentials, "url-credentials", counts);
        current = ApplyOne(current, Phone, "phone", counts);
        current = ApplyOne(current, IpAddress, "ip-address", counts);

        return new PiiResult(current, counts);
    }

    private static string ApplyOne(string input, Regex pattern, string className, Dictionary<string, int> counts)
    {
        var matchCount = 0;
        try
        {
            var replaced = pattern.Replace(input, _ =>
            {
                matchCount++;
                return $"[REDACTED:{className}]";
            });
            if (matchCount > 0)
                counts[className] = matchCount;
            return replaced;
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern took too long on adversarial input \u2014 leave the field unredacted
            // and record a sentinel so the orchestrator can flag the response with a
            // "redaction-timeout" entry in hygieneApplied. Not raising: returning the
            // original is safer than throwing 500 from a read endpoint.
            counts[$"{className}-timeout"] = 1;
            return input;
        }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>Result of <see cref="PiiDetector.Redact"/>.</summary>
internal sealed record PiiResult(string Text, IReadOnlyDictionary<string, int> Counts);
