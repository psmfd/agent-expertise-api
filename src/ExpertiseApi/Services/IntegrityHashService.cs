using System.Security.Cryptography;
using System.Text.Json;
using ExpertiseApi.Models;

namespace ExpertiseApi.Services;

/// <summary>
/// Canonical content hash for <see cref="ExpertiseEntry"/> rows and audit
/// BeforeHash/AfterHash values. The canonical field set is exactly
/// <c>{tenant, title, body, entryType, severity}</c> — a deliberate contract shared with
/// <see cref="BackupRecordHash"/>'s doc comment; never widen it here.
/// <para>
/// ADR-020: when an <see cref="IntegrityKey"/> is supplied the value is
/// <c>{keyId}:{lowercase-hex HMAC-SHA256}</c>, unforgeable by a database-only writer.
/// With a <c>null</c> key the value is the legacy bare-hex unkeyed SHA-256 (soft-require
/// phase; hard-require flip tracked in #490). The format prefix — colon present or
/// absent — tells verification which computation a stored value used.
/// </para>
/// </summary>
internal static class IntegrityHashService
{
    public static string Compute(
        string tenant,
        string title,
        string body,
        EntryType entryType,
        Severity severity) =>
        Compute(tenant, title, body, entryType, severity, key: null);

    public static string Compute(ExpertiseEntry entry) =>
        Compute(entry.Tenant, entry.Title, entry.Body, entry.EntryType, entry.Severity, key: null);

    public static string Compute(ExpertiseEntry entry, IntegrityKey? key) =>
        Compute(entry.Tenant, entry.Title, entry.Body, entry.EntryType, entry.Severity, key);

    public static string Compute(
        string tenant,
        string title,
        string body,
        EntryType entryType,
        Severity severity,
        IntegrityKey? key)
    {
        var canonical = CanonicalBytes(tenant, title, body, entryType, severity);

        if (key is null)
            return Convert.ToHexStringLower(SHA256.HashData(canonical));

        return $"{key.KeyId}:{Convert.ToHexStringLower(HMACSHA256.HashData(key.Key, canonical))}";
    }

    private static byte[] CanonicalBytes(
        string tenant,
        string title,
        string body,
        EntryType entryType,
        Severity severity)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            // Alphabetical key order — canonical JSON (RFC 8785-style) for stable hashes.
            writer.WriteStartObject();
            writer.WriteString("body", body);
            writer.WriteString("entryType", entryType.ToString());
            writer.WriteString("severity", severity.ToString());
            writer.WriteString("tenant", tenant);
            writer.WriteString("title", title);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
