using System.Security.Cryptography;
using System.Text.Json;

namespace ExpertiseApi.Services;

/// <summary>
/// MAC over an <see cref="Models.AuditCheckpoint"/>'s canonical fields (ADR-020). Same
/// format contract as <see cref="IntegrityHashService"/>: keyed → <c>{keyId}:{hex}</c>
/// HMAC-SHA256, unkeyed → bare-hex SHA-256 (soft-require phase); the colon tells
/// verification which computation a stored value used. The MAC binds the range, root,
/// row count, seal time, and the previous link's MAC, so a database-only writer cannot
/// re-seal a rewritten range or splice the chain without the key.
/// Canonicalization matches the codebase idiom: alphabetical keys via
/// <see cref="Utf8JsonWriter"/> (RFC 8785-style).
/// </summary>
internal static class CheckpointMacService
{
    public static string Compute(
        long seqFrom,
        long seqTo,
        int rowCount,
        string merkleRoot,
        string? prevCheckpointMac,
        DateTime createdAt,
        IntegrityKey? key)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("createdAt", createdAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("merkleRoot", merkleRoot);
            if (prevCheckpointMac is null)
                writer.WriteNull("prevCheckpointMac");
            else
                writer.WriteString("prevCheckpointMac", prevCheckpointMac);
            writer.WriteNumber("rowCount", rowCount);
            writer.WriteNumber("seqFrom", seqFrom);
            writer.WriteNumber("seqTo", seqTo);
            writer.WriteEndObject();
        }

        var canonical = stream.ToArray();

        if (key is null)
            return Convert.ToHexStringLower(SHA256.HashData(canonical));

        return $"{key.KeyId}:{Convert.ToHexStringLower(HMACSHA256.HashData(key.Key, canonical))}";
    }
}
