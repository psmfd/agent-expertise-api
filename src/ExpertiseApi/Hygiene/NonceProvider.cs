using System.Security.Cryptography;

namespace ExpertiseApi.Hygiene;

/// <summary>
/// Default <see cref="INonceProvider"/> backed by <see cref="RandomNumberGenerator"/>.
/// 16 bytes = 128 bits of entropy, meeting OWASP ASVS V3.2.2's session-token entropy
/// floor applied here for delimiter unguessability (security-review-expert recommendation
/// on #168). Collision probability is 1/2^128 \u2014 effectively zero across the deployment
/// lifetime, so no retry / collision-detection loop is necessary.
/// </summary>
internal sealed class NonceProvider : INonceProvider
{
    public string Mint()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }
}
