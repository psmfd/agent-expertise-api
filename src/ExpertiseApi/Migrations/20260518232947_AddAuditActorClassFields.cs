using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <summary>
    /// Adds the Part D C6 fields to the audit log: <c>ActorClass</c> (Human/Agent/Service),
    /// <c>AuthMethod</c> (Bearer/ApiKey/LocalDev), and <c>ActorClassHeader</c> (raw header).
    /// Existing rows backfill to <c>'Human'</c> via the column-level default; the default is
    /// then dropped so new audit writes must supply <c>ActorClass</c> explicitly (matches
    /// the convention from <see cref="AddTenantAuditFields"/>).
    /// <para>
    /// Reference: <c>docs/security/integration-threat-model.md</c> Part D C6.
    /// </para>
    /// </summary>
    public partial class AddAuditActorClassFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ActorClass is required and stored as text (matches Action/Visibility/ReviewState
            // convention). Default 'Human' on AddColumn backfills existing rows; we then drop
            // the column default so future inserts must supply the value explicitly.
            migrationBuilder.AddColumn<string>(
                name: "ActorClass",
                table: "ExpertiseAuditLogs",
                type: "text",
                nullable: false,
                defaultValue: "Human");

            // Belt-and-braces: ensure any pre-existing rows that somehow landed NULL get
            // 'Human'. AddColumn with a literal default applies on Postgres atomically so
            // this UPDATE is normally a no-op, but it makes the backfill intent explicit.
            migrationBuilder.Sql(@"UPDATE ""ExpertiseAuditLogs"" SET ""ActorClass"" = 'Human' WHERE ""ActorClass"" IS NULL OR ""ActorClass"" = '';");

            // Drop the column-level default so future INSERTs without an explicit ActorClass
            // value fail loudly rather than silently writing 'Human'. Mirrors the
            // AddTenantAuditFields pattern (see migration 20260428204727).
            migrationBuilder.Sql(@"ALTER TABLE ""ExpertiseAuditLogs"" ALTER COLUMN ""ActorClass"" DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "ActorClassHeader",
                table: "ExpertiseAuditLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthMethod",
                table: "ExpertiseAuditLogs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseAuditLogs_ActorClass_Timestamp",
                table: "ExpertiseAuditLogs",
                columns: new[] { "ActorClass", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpertiseAuditLogs_ActorClass_Timestamp",
                table: "ExpertiseAuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorClass",
                table: "ExpertiseAuditLogs");

            migrationBuilder.DropColumn(
                name: "ActorClassHeader",
                table: "ExpertiseAuditLogs");

            migrationBuilder.DropColumn(
                name: "AuthMethod",
                table: "ExpertiseAuditLogs");
        }
    }
}
