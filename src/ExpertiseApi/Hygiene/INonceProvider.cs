namespace ExpertiseApi.Hygiene;

/// <summary>
/// Mints per-response delimiter nonces for <see cref="IResponseHygiene"/>. Default
/// implementation is a cryptographically random 16-byte (128-bit) value hex-encoded;
/// tests substitute a deterministic provider via DI to make snapshots reproducible.
/// </summary>
internal interface INonceProvider
{
    /// <summary>
    /// Returns a fresh 32-character lowercase hex nonce (128-bit entropy).
    /// Unique per call; safe to assume zero collisions across responses.
    /// </summary>
    string Mint();
}
