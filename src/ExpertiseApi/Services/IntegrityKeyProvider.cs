using Prometheus;

namespace ExpertiseApi.Services;

/// <summary>
/// Active HMAC key for <see cref="IntegrityHashService"/> (ADR-020). <c>KeyId</c> is the
/// short identifier prefixed onto stored values (<c>{keyId}:{hex}</c>) so rotation can
/// keep retired keys resolvable for verification; <c>Key</c> is the raw material
/// (256-bit minimum, base64-decoded from config).
/// </summary>
internal sealed record IntegrityKey(string KeyId, byte[] Key);

internal interface IIntegrityKeyProvider
{
    /// <summary>
    /// The key used for all new IntegrityHash / audit BeforeHash/AfterHash computations,
    /// or <c>null</c> when the instance runs unkeyed (legacy SHA-256; soft-require phase
    /// per ADR-020, hard-require flip tracked in #490).
    /// </summary>
    IntegrityKey? ActiveKey { get; }
}

/// <summary>
/// Loads the ADR-020 integrity HMAC key once at startup. Fail-closed when configured:
/// a key that is present in config but unusable (missing file, invalid base64, too
/// short, malformed key id) aborts boot — the ADR-015 posture — because silently
/// falling back to the unkeyed hash would quietly write forgeable values. An entirely
/// unconfigured key is the sanctioned soft-require state: legacy unkeyed hashing plus
/// the <c>expertise_integrity_unkeyed</c> gauge and a startup warning.
/// </summary>
internal sealed class IntegrityKeyProvider : IIntegrityKeyProvider
{
    private static readonly Gauge UnkeyedGauge = Metrics.CreateGauge(
        "expertise_integrity_unkeyed",
        "1 when this instance computes legacy unkeyed integrity hashes (no Integrity:HmacKey configured), 0 when keyed (ADR-020).");

    public IntegrityKey? ActiveKey { get; }

    /// <summary>An always-unkeyed provider for tests and contexts with no configuration.</summary>
    public static IntegrityKeyProvider Unkeyed { get; } = new(null);

    private IntegrityKeyProvider(IntegrityKey? activeKey) => ActiveKey = activeKey;

    public static IntegrityKeyProvider Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var inline = configuration["Integrity:HmacKey"];
        var keyFile = configuration["Integrity:HmacKeyFile"];
        var keyId = configuration["Integrity:ActiveKeyId"];
        keyId = string.IsNullOrWhiteSpace(keyId) ? "k1" : keyId.Trim();

        // Inline wins when both are set — the EXPERTISE_API_TOKEN / _FILE idiom (#464).
        string material;
        string source;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            material = inline.Trim();
            source = "Integrity:HmacKey";
        }
        else if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile))
            {
                throw new InvalidOperationException(
                    $"Integrity:HmacKeyFile points to a missing file: {keyFile} (ADR-020 fail-closed).");
            }

            material = File.ReadAllText(keyFile).Trim();
            source = $"Integrity:HmacKeyFile ({keyFile})";
        }
        else
        {
            UnkeyedGauge.Set(1);
            return Unkeyed;
        }

        if (!IsValidKeyId(keyId))
        {
            throw new InvalidOperationException(
                $"Integrity:ActiveKeyId '{keyId}' is invalid — 1-32 chars of [A-Za-z0-9._-], no ':' (it prefixes stored hash values).");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(material);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{source} is not valid base64 (ADR-020 fail-closed).", ex);
        }

        if (key.Length < 32)
        {
            throw new InvalidOperationException(
                $"{source} must decode to at least 32 bytes (256-bit); got {key.Length} (ADR-020 fail-closed).");
        }

        UnkeyedGauge.Set(0);
        return new IntegrityKeyProvider(new IntegrityKey(keyId, key));
    }

    private static bool IsValidKeyId(string keyId)
    {
        if (keyId.Length is < 1 or > 32)
            return false;
        foreach (var c in keyId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }
}
