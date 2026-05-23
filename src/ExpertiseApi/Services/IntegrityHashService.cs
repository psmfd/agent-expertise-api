using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using ExpertiseApi.Models;

namespace ExpertiseApi.Services;

internal static class IntegrityHashService
{
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Output is hex-encoded SHA-256 (0-9, a-f only). The Turkish-locale gotcha that motivates the rule cannot apply to ASCII hex characters.")]
    public static string Compute(
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

        var hash = SHA256.HashData(stream.ToArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Compute(ExpertiseEntry entry) =>
        Compute(entry.Tenant, entry.Title, entry.Body, entry.EntryType, entry.Severity);
}
