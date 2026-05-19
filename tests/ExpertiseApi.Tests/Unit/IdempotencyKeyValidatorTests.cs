using ExpertiseApi.Services.Idempotency;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// IETF <c>draft-ietf-httpapi-idempotency-key-header-06</c> §2.2 charset
/// conformance for <see cref="IdempotencyKeyValidator"/>.
/// </summary>
public class IdempotencyKeyValidatorTests
{
    [Theory]
    [InlineData("simple-key-123")]
    [InlineData("UUID-LIKE-abc-DEF")]
    [InlineData("a")]                                   // 1 char
    [InlineData("k!\"#$%&'()*+,-./:;<=>?@[]^_`{|}~")]   // every VCHAR
    public void Valid_keys_pass(string key)
    {
        var result = IdempotencyKeyValidator.Validate(key);
        result.IsValid.Should().BeTrue($"'{key}' is per IETF §2.2");
        result.Reason.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_or_empty_is_rejected(string? key)
    {
        var result = IdempotencyKeyValidator.Validate(key);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("non-empty");
    }

    [Fact]
    public void Length_256_is_rejected()
    {
        var result = IdempotencyKeyValidator.Validate(new string('k', 256));
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("length");
    }

    [Fact]
    public void Length_255_at_boundary_is_accepted()
    {
        var result = IdempotencyKeyValidator.Validate(new string('k', 255));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("newline\nhere")]
    [InlineData("cr\rhere")]
    public void Whitespace_is_rejected(string key)
    {
        var result = IdempotencyKeyValidator.Validate(key);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("whitespace");
    }

    [Theory]
    [InlineData("unicode-é")]
    [InlineData("control-\u0001-byte")]
    [InlineData("DEL-\u007F")]
    [InlineData("nul-\u0000-byte")]
    public void Non_vchar_is_rejected(string key)
    {
        var result = IdempotencyKeyValidator.Validate(key);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().Contain("ASCII printable");
    }

    [Fact]
    public void Rejection_reason_does_not_echo_key_value()
    {
        // Defensive: reason strings must never contain the user-supplied key
        // (avoids reflection-amplified XSS in any downstream log viewer). Use
        // a key that IS invalid (contains whitespace) so the validator returns
        // a reason, then assert the value is not in that reason.
        const string maliciousProbe = "<script> alert(1)</script>";
        var result = IdempotencyKeyValidator.Validate(maliciousProbe);
        result.IsValid.Should().BeFalse();
        result.Reason.Should().NotContain(maliciousProbe);
        result.Reason.Should().NotContain("<script>");
    }
}
