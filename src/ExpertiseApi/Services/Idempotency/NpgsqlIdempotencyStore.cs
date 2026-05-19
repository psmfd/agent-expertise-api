using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ExpertiseApi.Services.Idempotency;

/// <summary>
/// Npgsql-backed <see cref="IIdempotencyStore"/> implementing the ADR-010
/// concurrency contract: <c>INSERT … ON CONFLICT (tenant, key) DO NOTHING</c>
/// for placeholder reservation, followed by <c>SELECT … FOR UPDATE</c> within
/// the same transaction on conflict. PgBouncer-safe under transaction
/// pooling: every method acquires one connection, runs one transaction, and
/// releases.
/// <para>
/// Singleton lifetime — the underlying <see cref="NpgsqlDataSource"/> manages
/// its own pool and is the natural sharing primitive. The store carries no
/// per-request state.
/// </para>
/// </summary>
internal sealed class NpgsqlIdempotencyStore : IIdempotencyStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlIdempotencyStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async Task<(IdempotencyLookupOutcome Outcome, IdempotencyReplayPayload? Payload)> TryReserveAsync(
        string tenant,
        string key,
        string requestHash,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        // Insert placeholder. status_code = 0 + empty hash mark "in-flight";
        // PersistAsync UPDATEs these to the real values on response completion.
        // On conflict, the row already exists (either in-flight from a
        // concurrent request or finalized from a prior request) — fall through
        // to the SELECT branch below.
        await using (var insertCmd = new NpgsqlCommand(
            @"INSERT INTO idempotency_records
                (tenant, key, request_hash, status_code, response_body_hash, response_body, response_headers, response_content_type)
              VALUES (@tenant, @key, @hash, 0, '', NULL, NULL, NULL)
              ON CONFLICT (tenant, key) DO NOTHING;",
            conn, tx))
        {
            insertCmd.Parameters.AddWithValue("tenant", tenant);
            insertCmd.Parameters.AddWithValue("key", key);
            insertCmd.Parameters.AddWithValue("hash", requestHash);
            var rows = await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 1)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (IdempotencyLookupOutcome.Reserved, null);
            }
        }

        // Existing row. SELECT FOR UPDATE blocks momentarily if a concurrent
        // PersistAsync UPDATE is in flight, which is exactly what we want —
        // we read the finalized state. Placeholder rows (status_code = 0)
        // surface as "in-flight, no payload"; the filter treats that as
        // 409 (concurrent request with same key still executing).
        await using (var selectCmd = new NpgsqlCommand(
            @"SELECT request_hash, status_code, response_body_hash, response_body,
                     response_headers, response_content_type
              FROM idempotency_records
              WHERE tenant = @tenant AND key = @key
              FOR UPDATE;",
            conn, tx))
        {
            selectCmd.Parameters.AddWithValue("tenant", tenant);
            selectCmd.Parameters.AddWithValue("key", key);
            await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // Vanishingly rare: row existed long enough to fail the INSERT
                // but was deleted (GC) before the SELECT. Treat as Reserved on
                // a fresh attempt would be unsafe (no row to UPDATE later); the
                // safer move is to fail the request and let the caller retry.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return (IdempotencyLookupOutcome.HitMismatch, null);
            }

            var existingHash = reader.GetString(0);
            if (!string.Equals(existingHash, requestHash, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return (IdempotencyLookupOutcome.HitMismatch, null);
            }

            var statusCode = reader.GetInt32(1);
            if (statusCode == 0)
            {
                // Placeholder — concurrent in-flight request with same key+hash.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return (IdempotencyLookupOutcome.HitMatch, null);
            }

            var bodyHash = reader.GetString(2);
            byte[]? body = await reader.IsDBNullAsync(3, ct).ConfigureAwait(false)
                ? null
                : (byte[])reader.GetValue(3);
            IReadOnlyDictionary<string, string>? headers = null;
            if (!await reader.IsDBNullAsync(4, ct).ConfigureAwait(false))
            {
                var json = reader.GetString(4);
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            string? contentType = await reader.IsDBNullAsync(5, ct).ConfigureAwait(false)
                ? null
                : reader.GetString(5);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (
                IdempotencyLookupOutcome.HitMatch,
                new IdempotencyReplayPayload(
                    StatusCode: statusCode,
                    ContentType: contentType,
                    Headers: headers,
                    Body: body,
                    BodyOmittedDueToSize: body is null && !string.IsNullOrEmpty(bodyHash)));
        }
    }

    /// <inheritdoc />
    public async Task PersistAsync(
        string tenant,
        string key,
        int statusCode,
        string responseBodyHash,
        byte[]? responseBody,
        string? contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            @"UPDATE idempotency_records
              SET status_code = @status,
                  response_body_hash = @hash,
                  response_body = @body,
                  response_headers = @headers::jsonb,
                  response_content_type = @ctype
              WHERE tenant = @tenant AND key = @key;",
            conn);
        cmd.Parameters.AddWithValue("tenant", tenant);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("status", statusCode);
        cmd.Parameters.AddWithValue("hash", responseBodyHash);

        var bodyParam = cmd.Parameters.Add("body", NpgsqlDbType.Bytea);
        bodyParam.Value = (object?)responseBody ?? DBNull.Value;

        var headersParam = cmd.Parameters.Add("headers", NpgsqlDbType.Jsonb);
        headersParam.Value = headers is null
            ? DBNull.Value
            : JsonSerializer.Serialize(headers);

        var ctypeParam = cmd.Parameters.Add("ctype", NpgsqlDbType.Text);
        ctypeParam.Value = (object?)contentType ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> SweepExpiredAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            @"DELETE FROM idempotency_records WHERE created_at < @cutoff;",
            conn);
        cmd.Parameters.AddWithValue("cutoff", olderThan.UtcDateTime);
        var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows;
    }

    // Reserved for future operator diagnostic — not on the IIdempotencyStore
    // contract because it is not used by the filter hot path.
    internal static string FormatCutoffForLog(DateTimeOffset cutoff) =>
        cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
