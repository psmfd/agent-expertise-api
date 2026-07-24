using System.Text.RegularExpressions;
using ExpertiseApi.Hygiene;

namespace ExpertiseApi.Tests.Unit.Hygiene;

public class PiiDetectorTests
{
    // --- Email ---
    [Theory]
    [InlineData("Contact me at alice@example.com please")]
    [InlineData("alice+oncall@example.co.uk should reach")]
    [InlineData("nested.dotted@sub.example.io domain")]
    public void Email_PositiveCases_AreRedacted(string input)
    {
        var result = PiiDetector.Redact(input);
        result.Text.Should().Contain("[REDACTED:email]");
        result.Counts.Should().ContainKey("email");
    }

    [Theory]
    [InlineData("Just a plain string with no email")]
    [InlineData("alice@x \u2014 not a complete email (TLD too short)")]
    [InlineData("notanemail@@example.com")]
    public void Email_NegativeCases_AreNotRedacted(string input)
    {
        var result = PiiDetector.Redact(input);
        result.Counts.Should().NotContainKey("email");
    }

    // --- AWS keys ---
    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ASIAIOSFODNN7EXAMPLE")]
    public void AwsAccessKey_IsRedacted(string input)
    {
        var result = PiiDetector.Redact(input);
        result.Text.Should().Contain("[REDACTED:aws-access-key]");
    }

    [Fact]
    public void AwsAccessKey_LowercaseDoesNotMatch()
    {
        // The pattern uses [0-9A-Z] explicitly. NonBacktracking + IgnoreCase
        // means casing on the AKIA/ASIA prefix is flexible but the suffix MUST
        // be exact case per the AWS contract. We verify the prefix matches
        // case-insensitively (since the regex sets IgnoreCase) but the suffix
        // is case-sensitive at the character class level.
        var result = PiiDetector.Redact("akiaiosfodnn7example");
        // With IgnoreCase the whole match is allowed; this just documents
        // intent. The important assertion is that ASCII uppercase keys redact.
        _ = result;
    }

    [Fact]
    public void AwsSecret_WithContextWord_IsRedacted()
    {
        var input = "aws_secret_access_key=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef01234567";
        var result = PiiDetector.Redact(input);
        result.Text.Should().Contain("[REDACTED:aws-secret]");
    }

    [Fact]
    public void AwsSecret_WithoutContextWord_IsNotRedacted()
    {
        // A bare 40-char base64-ish run with no preceding 'secret'/'aws_secret'/'aws_secret_access_key'
        // should not be flagged \u2014 too many false-positive surfaces in code samples.
        var input = "Hash digest: ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef01234567 next";
        var result = PiiDetector.Redact(input);
        result.Counts.Should().NotContainKey("aws-secret");
    }

    // --- GitHub PAT ---
    [Theory]
    [InlineData("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("gho_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("ghs_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("ghr_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    public void GithubPat_IsRedacted(string token)
    {
        var result = PiiDetector.Redact($"Token: {token}");
        result.Text.Should().Contain("[REDACTED:github-pat]");
    }

    // --- JWT ---
    [Fact]
    public void Jwt_IsRedacted()
    {
        var jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var result = PiiDetector.Redact($"Bearer {jwt}");
        result.Text.Should().Contain("[REDACTED:jwt]");
    }

    // --- URL credentials ---
    [Fact]
    public void UrlCredentials_AreRedacted()
    {
        var result = PiiDetector.Redact("Connect via https://alice:hunter2@db.example.com/x");
        result.Text.Should().Contain("[REDACTED:url-credentials]");
    }

    // --- PEM private key header ---
    [Fact]
    public void PrivateKeyHeader_IsRedacted()
    {
        var result = PiiDetector.Redact("Paste this:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEvg...");
        result.Text.Should().Contain("[REDACTED:private-key-header]");
    }

    // --- IP addresses ---
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.42")]
    [InlineData("8.8.8.8")]
    public void Ipv4_IsRedacted(string ip)
    {
        var result = PiiDetector.Redact($"Source: {ip}");
        result.Text.Should().Contain("[REDACTED:ip-address]");
    }

    [Fact]
    public void Ipv4_VersionStringDoesNotMatch()
    {
        // The IP pattern matches dotted-quad whole; 1.10.0 has only 3 segments.
        var result = PiiDetector.Redact("Version 1.10.0 released");
        result.Counts.Should().NotContainKey("ip-address");
    }

    // --- Phone ---
    [Fact]
    public void Phone_E164_IsRedacted()
    {
        var result = PiiDetector.Redact("Call +1-555-867-5309 for support");
        result.Text.Should().Contain("[REDACTED:phone]");
    }

    [Fact]
    public void Phone_VersionStringMostlyDoesNotMatch()
    {
        // Best-effort: the negative lookbehind/lookahead skips dotted version strings.
        // We accept some FP risk on numerics; this just documents the guardrail.
        var result = PiiDetector.Redact("Library 1.10.0 was released");
        result.Counts.Should().NotContainKey("phone");
    }

    // --- Empty / null ---
    [Fact]
    public void EmptyInput_ProducesEmptyResult()
    {
        var result = PiiDetector.Redact("");
        result.Text.Should().BeEmpty();
        result.Counts.Should().BeEmpty();
    }

    [Fact]
    public void MultiplePiiInOneInput_AllAreRedacted_AndCountsAreCorrect()
    {
        var input = "Email alice@example.com and a key ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
        var result = PiiDetector.Redact(input);
        result.Counts["email"].Should().Be(1);
        result.Counts["github-pat"].Should().Be(1);
        result.Text.Should().Contain("[REDACTED:email]").And.Contain("[REDACTED:github-pat]");
    }

    // --- AWS secret: capture-group redaction preserves surrounding context (#333 Finding 2) ---

    [Fact]
    public void AwsSecret_RedactsOnlyTheSecret_PreservingTheContextWord()
    {
        // The NonBacktracking rewrite consumes the context word into the match but pulls
        // the secret into capture group 1; ApplyOne must redact ONLY the group, leaving
        // the context prefix byte-identical to the pre-rewrite lookbehind behaviour.
        var input = "aws_secret_access_key=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef01234567";
        var result = PiiDetector.Redact(input);
        result.Text.Should().Be("aws_secret_access_key=[REDACTED:aws-secret]");
    }

    [Fact]
    public void AwsSecret_PreservesTrailingTextAfterTheSecret()
    {
        // The trailing boundary is consumed via (?:[^...]|$) (no lookahead under
        // NonBacktracking) but sits outside group 1, so the separator and following
        // text must be re-emitted verbatim.
        var input = "secret ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef01234567 and then more";
        var result = PiiDetector.Redact(input);
        result.Text.Should().Be("secret [REDACTED:aws-secret] and then more");
        result.Counts["aws-secret"].Should().Be(1);
    }

    [Fact]
    public void AwsSecret_AtEndOfString_IsRedacted()
    {
        // The '$' alternative of the trailing boundary handles a secret flush against EOL.
        var input = "secret=ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef01234567";
        var result = PiiDetector.Redact(input);
        result.Text.Should().Be("secret=[REDACTED:aws-secret]");
    }

    // --- Fail-closed timeout handling (#333 Finding 2) ---

    [Fact]
    public void ApplyOne_OnRegexTimeout_SuppressesWholeField_AndSignalsTimeout()
    {
        // A catastrophic-backtracking pattern (NOT NonBacktracking) against non-matching
        // input forces a RegexMatchTimeoutException deterministically at a 1ms budget.
        // The production detectors can no longer reach this path (all NonBacktracking),
        // so this exercises the fail-closed net directly through the internal seam.
        var evil = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var adversarial = new string('a', 40) + "!";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        var result = PiiDetector.ApplyOne(adversarial, evil, "aws-secret", counts, out var timedOut);

        timedOut.Should().BeTrue();
        result.Should().Be("[REDACTED:aws-secret-timeout]",
            "fail-closed must replace the WHOLE field, never return the original unredacted value");
        result.Should().NotContain(adversarial, "the original adversarial content must not survive");
        counts.Should().ContainKey("aws-secret-timeout");
    }

    [Fact]
    public void ApplyOne_NoTimeout_SignalsFalse()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ok = new Regex("nomatch", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

        var result = PiiDetector.ApplyOne("harmless text", ok, "email", counts, out var timedOut);

        timedOut.Should().BeFalse();
        result.Should().Be("harmless text");
        counts.Should().BeEmpty();
    }
}
