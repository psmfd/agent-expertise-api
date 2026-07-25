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

    /// <summary>
    /// Resolves a stored value's key-id prefix to the active key or an
    /// <c>Integrity:RetiredKeys</c> entry; <c>null</c> when the id is unknown
    /// (verification reports it as a mismatch, never a pass).
    /// </summary>
    IntegrityKey? ResolveKey(string keyId);
}

/// <summary>
/// Loads the ADR-020 integrity HMAC keys once at startup. Fail-closed when configured:
/// a key that is present in config but unusable (missing file, invalid base64, too
/// short, malformed key id) aborts boot — the ADR-015 posture — because silently
/// falling back to the unkeyed hash would quietly write forgeable values. An entirely
/// unconfigured active key is the sanctioned soft-require state: legacy unkeyed hashing
/// plus the <c>expertise_integrity_unkeyed</c> gauge and a startup warning.
/// <c>Integrity:RetiredKeys</c> (<c>{id: base64}</c>) keeps rotated-out keys resolvable
/// so <c>verify</c> can check values sealed under a previous key.
/// </summary>
internal sealed class IntegrityKeyProvider : IIntegrityKeyProvider
{
    private static readonly Gauge UnkeyedGauge = Metrics.CreateGauge(
        "expertise_integrity_unkeyed",
        "1 when this instance computes legacy unkeyed integrity hashes (no Integrity:HmacKey configured), 0 when keyed (ADR-020).");

    public IntegrityKey? ActiveKey { get; }

    private readonly IReadOnlyDictionary<string, IntegrityKey> _retiredKeys;

    /// <summary>An always-unkeyed provider for tests and contexts with no configuration.</summary>
    public static IntegrityKeyProvider Unkeyed { get; } =
        new(null, new Dictionary<string, IntegrityKey>(StringComparer.Ordinal));

    private IntegrityKeyProvider(
        IntegrityKey? activeKey,
        IReadOnlyDictionary<string, IntegrityKey> retiredKeys)
    {
        ActiveKey = activeKey;
        _retiredKeys = retiredKeys;
    }

    public IntegrityKey? ResolveKey(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);

        if (ActiveKey is not null && string.Equals(ActiveKey.KeyId, keyId, StringComparison.Ordinal))
            return ActiveKey;

        return _retiredKeys.TryGetValue(keyId, out var key) ? key : null;
    }

    public static IntegrityKeyProvider Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var retired = LoadRetiredKeys(configuration);

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
            return retired.Count == 0
                ? Unkeyed
                : new IntegrityKeyProvider(null, retired);
        }

        if (!IsValidKeyId(keyId))
        {
            throw new InvalidOperationException(
                $"Integrity:ActiveKeyId '{keyId}' is invalid — 1-32 chars of [A-Za-z0-9._-], no ':' (it prefixes stored hash values).");
        }

        if (retired.ContainsKey(keyId))
        {
            throw new InvalidOperationException(
                $"Integrity:RetiredKeys contains the active key id '{keyId}' — a retired id must not shadow the active key (ADR-020 fail-closed).");
        }

        var key = DecodeKeyMaterial(material, source);

        UnkeyedGauge.Set(0);
        return new IntegrityKeyProvider(new IntegrityKey(keyId, key), retired);
    }

    private static Dictionary<string, IntegrityKey> LoadRetiredKeys(IConfiguration configuration)
    {
        var retired = new Dictionary<string, IntegrityKey>(StringComparer.Ordinal);
        foreach (var child in configuration.GetSection("Integrity:RetiredKeys").GetChildren())
        {
            var id = child.Key.Trim();
            if (!IsValidKeyId(id))
            {
                throw new InvalidOperationException(
                    $"Integrity:RetiredKeys key id '{child.Key}' is invalid — 1-32 chars of [A-Za-z0-9._-], no ':' (ADR-020 fail-closed).");
            }

            if (string.IsNullOrWhiteSpace(child.Value))
            {
                throw new InvalidOperationException(
                    $"Integrity:RetiredKeys:{id} has empty key material (ADR-020 fail-closed).");
            }

            retired[id] = new IntegrityKey(
                id, DecodeKeyMaterial(child.Value.Trim(), $"Integrity:RetiredKeys:{id}"));
        }

        return retired;
    }

    private static byte[] DecodeKeyMaterial(string material, string source)
    {
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

        return key;
    }

    private static bool IsValidKeyId(string keyId) =>
        keyId.Length is >= 1 and <= 32
        && keyId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
