using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <summary>
    /// Adds the <c>idempotency_records</c> table backing the Part D C3 control
    /// (ADR-010). The table is intentionally <strong>not</strong> mapped through
    /// <see cref="ExpertiseApi.Data.ExpertiseDbContext"/>: <see cref="ExpertiseApi.Services.Idempotency.IIdempotencyStore"/>
    /// uses a singleton <c>NpgsqlDataSource</c> over raw SQL so the
    /// idempotency hot path stays out of EF's change tracker, MSL graph, and
    /// per-request scope ceremony (singleton lifetime is one of ADR-010's
    /// load-bearing decisions).
    /// <para>
    /// Because the model is unchanged, the accompanying <c>.Designer.cs</c>
    /// snapshot is a verbatim copy of the previous migration's snapshot — EF
    /// is unaware of this table and will not "drift-correct" it on a future
    /// <c>dotnet ef migrations add</c>.
    /// </para>
    /// <para>
    /// Schema:
    /// <list type="bullet">
    /// <item><c>tenant</c> (text) — partition column from TenantContext; cross-tenant key reuse is independent.</item>
    /// <item><c>key</c> (text) — caller-supplied <c>Idempotency-Key</c> header (1–255 ASCII printable, no whitespace).</item>
    /// <item><c>request_hash</c> (text) — SHA-256 hex of method ‖ route-template ‖ tenant ‖ principal-sub ‖ raw body bytes; mismatch returns 409.</item>
    /// <item><c>status_code</c> (int) — original HTTP status; 2xx and deterministic 4xx (400, 409, 422) only (ADR-010 amendment 2026-05-19).</item>
    /// <item><c>response_body_hash</c> (text) — SHA-256 hex of the buffered body, retained even when body itself is dropped on overflow.</item>
    /// <item><c>response_body</c> (bytea) — original response body bytes, ≤ 64 KiB; NULL when overflow rule fires (replay then returns 200 with <c>Warning</c> header per IETF draft §2.5).</item>
    /// <item><c>created_at</c> (timestamptz) — set by store on insert; indexed for the 24h GC sweep.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Reference: <c>adrs/010-idempotency-key-handling.md</c>; <c>docs/security/integration-threat-model.md</c> Part D C3.
    /// </para>
    /// </summary>
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Raw SQL rather than CreateTable/AddColumn so the column names stay
            // lowercase-snake (idiomatic for the raw-SQL store), and so EF Core's
            // model snapshot machinery is not tempted to round-trip and rename
            // them on a future scaffold. The table sits outside the EF model
            // entirely; see class-level doc.
            migrationBuilder.Sql(@"
                CREATE TABLE idempotency_records (
                    tenant              text          NOT NULL,
                    key                 text          NOT NULL,
                    request_hash        text          NOT NULL,
                    status_code         integer       NOT NULL,
                    response_body_hash  text          NOT NULL,
                    response_body       bytea         NULL,
                    response_headers    jsonb         NULL,
                    response_content_type text        NULL,
                    created_at          timestamptz   NOT NULL DEFAULT now(),
                    CONSTRAINT pk_idempotency_records PRIMARY KEY (tenant, key)
                );

                CREATE INDEX ix_idempotency_records_created_at
                    ON idempotency_records (created_at);

                COMMENT ON TABLE idempotency_records IS
                    'Part D C3 — Idempotency-Key replay store; ADR-010.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS idempotency_records;");
        }
    }
}
