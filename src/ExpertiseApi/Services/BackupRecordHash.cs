using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ExpertiseApi.Cli;

namespace ExpertiseApi.Services;

/// <summary>
/// Full-record canonical hash for backup artifacts (ADR-012). Deliberately a SIBLING of
/// <see cref="IntegrityHashService"/>, never an extension of it: <c>Compute</c> hashes
/// exactly {tenant, title, body, entryType, severity} and is load-bearing for the
/// dedup-equality contract — widening it would silently change duplicate detection.
/// This hash covers every content-bearing field of a record (including <c>Id</c>, which
/// binds the hash to the record's identity) so a restore can localize a single tampered
/// record and quarantine it. Embeddings are excluded — derived data, outside the trust
/// boundary, regenerable via <c>reembed</c>.
///
/// Canonicalization matches <see cref="IntegrityHashService"/>'s idiom: alphabetical key
/// order via <see cref="Utf8JsonWriter"/> (RFC 8785-style), SHA-256, lowercase hex.
/// Timestamps are canonicalized as UTC round-trip ("O") strings; tags are sorted
/// ordinally so storage-layer array order can never affect the hash.
/// </summary>
internal static class BackupRecordHash
{
    public static string ComputeEntry(BackupEntryRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteNullable(writer, "authorAgent", r.AuthorAgent);
            writer.WriteString("authorPrincipal", r.AuthorPrincipal);
            writer.WriteString("body", r.Body);
            writer.WriteString("createdAt", Canonical(r.CreatedAt));
            WriteNullable(writer, "deprecatedAt", Canonical(r.DeprecatedAt));
            writer.WriteString("domain", r.Domain);
            writer.WriteString("entryType", r.EntryType);
            writer.WriteString("id", r.Id.ToString("D"));
            WriteNullable(writer, "rejectionReason", r.RejectionReason);
            writer.WriteString("reviewState", r.ReviewState);
            WriteNullable(writer, "reviewedAt", Canonical(r.ReviewedAt));
            WriteNullable(writer, "reviewedBy", r.ReviewedBy);
            writer.WriteString("severity", r.Severity);
            writer.WriteString("source", r.Source);
            WriteNullable(writer, "sourceVersion", r.SourceVersion);
            writer.WriteStartArray("tags");
            foreach (var tag in r.Tags.Order(StringComparer.Ordinal))
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
            writer.WriteString("tenant", r.Tenant);
            writer.WriteString("title", r.Title);
            writer.WriteString("updatedAt", Canonical(r.UpdatedAt));
            writer.WriteString("visibility", r.Visibility);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string ComputeAudit(BackupAuditRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", r.Action);
            writer.WriteString("actorClass", r.ActorClass);
            WriteNullable(writer, "actorClassHeader", r.ActorClassHeader);
            WriteNullable(writer, "afterHash", r.AfterHash);
            WriteNullable(writer, "agent", r.Agent);
            WriteNullable(writer, "authMethod", r.AuthMethod);
            WriteNullable(writer, "beforeHash", r.BeforeHash);
            writer.WriteString("entryId", r.EntryId.ToString("D"));
            writer.WriteString("id", r.Id.ToString("D"));
            WriteNullable(writer, "ipAddress", r.IpAddress);
            writer.WriteString("principal", r.Principal);
            writer.WriteString("tenant", r.Tenant);
            writer.WriteString("timestamp", Canonical(r.Timestamp));
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static string Canonical(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Canonical(DateTime? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void WriteNullable(Utf8JsonWriter writer, string key, string? value)
    {
        if (value is null)
            writer.WriteNull(key);
        else
            writer.WriteString(key, value);
    }
}
