using ExpertiseApi.Services;
using Microsoft.Extensions.Configuration;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// ADR-020 key-loading contract: fail-closed when a key is configured but unusable,
/// soft-require (null key) when nothing is configured, inline-wins-over-file per the
/// EXPERTISE_API_TOKEN/_FILE idiom (#464).
/// </summary>
public class IntegrityKeyProviderTests : IDisposable
{
    private static readonly string ValidKeyBase64 =
        Convert.ToBase64String(Enumerable.Repeat((byte)0x42, 32).ToArray());

    private readonly string _workDir =
        Directory.CreateTempSubdirectory("integrity-key-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_workDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Load_Unconfigured_ReturnsNullKey()
    {
        var provider = IntegrityKeyProvider.Load(Config());

        provider.ActiveKey.Should().BeNull();
    }

    [Fact]
    public void Load_InlineKey_ResolvesWithDefaultKeyId()
    {
        var provider = IntegrityKeyProvider.Load(Config(("Integrity:HmacKey", ValidKeyBase64)));

        provider.ActiveKey.Should().NotBeNull();
        provider.ActiveKey!.KeyId.Should().Be("k1");
        provider.ActiveKey.Key.Should().HaveCount(32);
    }

    [Fact]
    public void Load_KeyFile_ResolvesTrimmingWhitespace()
    {
        var path = Path.Combine(_workDir, "hmac.key");
        File.WriteAllText(path, ValidKeyBase64 + "\n");

        var provider = IntegrityKeyProvider.Load(Config(("Integrity:HmacKeyFile", path)));

        provider.ActiveKey.Should().NotBeNull();
        provider.ActiveKey!.Key.Should().Equal(Enumerable.Repeat((byte)0x42, 32));
    }

    [Fact]
    public void Load_InlineWinsOverFile()
    {
        var path = Path.Combine(_workDir, "hmac-file.key");
        File.WriteAllText(path, Convert.ToBase64String(Enumerable.Repeat((byte)0x01, 32).ToArray()));

        var provider = IntegrityKeyProvider.Load(Config(
            ("Integrity:HmacKey", ValidKeyBase64),
            ("Integrity:HmacKeyFile", path)));

        provider.ActiveKey!.Key.Should().Equal(Enumerable.Repeat((byte)0x42, 32));
    }

    [Fact]
    public void Load_CustomActiveKeyId_IsApplied()
    {
        var provider = IntegrityKeyProvider.Load(Config(
            ("Integrity:HmacKey", ValidKeyBase64),
            ("Integrity:ActiveKeyId", "prod-2026a")));

        provider.ActiveKey!.KeyId.Should().Be("prod-2026a");
    }

    [Fact]
    public void Load_MissingKeyFile_FailsClosed()
    {
        var act = () => IntegrityKeyProvider.Load(Config(
            ("Integrity:HmacKeyFile", Path.Combine(_workDir, "does-not-exist"))));

        act.Should().Throw<InvalidOperationException>().WithMessage("*missing file*");
    }

    [Fact]
    public void Load_InvalidBase64_FailsClosed()
    {
        var act = () => IntegrityKeyProvider.Load(Config(("Integrity:HmacKey", "not-base64!!!")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid base64*");
    }

    [Fact]
    public void Load_ShortKey_FailsClosed()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        var act = () => IntegrityKeyProvider.Load(Config(("Integrity:HmacKey", shortKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Theory]
    [InlineData("has:colon")]
    [InlineData("has space")]
    [InlineData("way-too-long-key-identifier-value-over-32")]
    public void Load_InvalidKeyId_FailsClosed(string keyId)
    {
        var act = () => IntegrityKeyProvider.Load(Config(
            ("Integrity:HmacKey", ValidKeyBase64),
            ("Integrity:ActiveKeyId", keyId)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ActiveKeyId*");
    }

    [Fact]
    public void Unkeyed_SingletonHasNullKey()
    {
        IntegrityKeyProvider.Unkeyed.ActiveKey.Should().BeNull();
    }
}
