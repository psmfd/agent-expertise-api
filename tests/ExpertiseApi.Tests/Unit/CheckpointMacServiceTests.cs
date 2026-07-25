using ExpertiseApi.Services;

namespace ExpertiseApi.Tests.Unit;

/// <summary>
/// ADR-020 checkpoint MAC format contract: keyed → <c>{keyId}:{hex}</c> HMAC, unkeyed →
/// bare-hex SHA-256, and every canonical field (range, root, row count, seal time,
/// previous link) must influence the value — a field the MAC ignores is a field a
/// database-only writer can rewrite undetected.
/// </summary>
public class CheckpointMacServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    private static IntegrityKey TestKey(string keyId = "k1", byte fill = 0x42)
    {
        var key = new byte[32];
        Array.Fill(key, fill);
        return new IntegrityKey(keyId, key);
    }

    private static string Compute(
        long seqFrom = 1, long seqTo = 10, int rowCount = 10, string root = "aa",
        string? prevMac = null, DateTime? createdAt = null, IntegrityKey? key = null) =>
        CheckpointMacService.Compute(seqFrom, seqTo, rowCount, root, prevMac, createdAt ?? CreatedAt, key);

    [Fact]
    public void Compute_Keyed_HasKeyIdPrefixAndHexMac()
    {
        Compute(key: TestKey()).Should().MatchRegex("^k1:[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_Unkeyed_IsBareHex()
    {
        Compute().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        Compute(key: TestKey()).Should().Be(Compute(key: TestKey()));
        Compute().Should().Be(Compute());
    }

    [Fact]
    public void Compute_EveryCanonicalField_InfluencesTheMac()
    {
        var baseline = Compute(key: TestKey());

        Compute(seqFrom: 2, key: TestKey()).Should().NotBe(baseline);
        Compute(seqTo: 11, key: TestKey()).Should().NotBe(baseline);
        Compute(rowCount: 9, key: TestKey()).Should().NotBe(baseline);
        Compute(root: "bb", key: TestKey()).Should().NotBe(baseline);
        Compute(prevMac: "cc", key: TestKey()).Should().NotBe(baseline);
        Compute(createdAt: CreatedAt.AddSeconds(1), key: TestKey()).Should().NotBe(baseline);
    }

    [Fact]
    public void Compute_DiffersByKeyMaterial_AndFromUnkeyed()
    {
        var a = Compute(key: TestKey(fill: 0x01));
        var b = Compute(key: TestKey(fill: 0x02));
        var unkeyed = Compute();

        a.Should().NotBe(b);
        a.Should().NotEndWith(unkeyed, "the MAC must not equal the plain hash even ignoring the prefix");
    }
}
