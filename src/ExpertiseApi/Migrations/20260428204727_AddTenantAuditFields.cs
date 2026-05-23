using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorAgent",
                table: "ExpertiseEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorPrincipal",
                table: "ExpertiseEntries",
                type: "text",
                nullable: false,
                defaultValue: "pre-rebuild");

            migrationBuilder.AddColumn<string>(
                name: "IntegrityHash",
                table: "ExpertiseEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "ExpertiseEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewState",
                table: "ExpertiseEntries",
                type: "text",
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "ExpertiseEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "ExpertiseEntries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tenant",
                table: "ExpertiseEntries",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "Visibility",
                table: "ExpertiseEntries",
                type: "text",
                nullable: false,
                defaultValue: "Private");

            // Drop column-level defaults for Tenant and AuthorPrincipal after backfill.
            // New rows must supply both explicitly — preventing silent "legacy"/"pre-rebuild" writes.
            // Visibility and ReviewState retain their defaults as secure-by-default values.
            migrationBuilder.Sql("""ALTER TABLE "ExpertiseEntries" ALTER COLUMN "Tenant" DROP DEFAULT;""");
            migrationBuilder.Sql("""ALTER TABLE "ExpertiseEntries" ALTER COLUMN "AuthorPrincipal" DROP DEFAULT;""");

            migrationBuilder.CreateTable(
                name: "ExpertiseAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tenant = table.Column<string>(type: "text", nullable: false),
                    Principal = table.Column<string>(type: "text", nullable: false),
                    Agent = table.Column<string>(type: "text", nullable: true),
                    BeforeHash = table.Column<string>(type: "text", nullable: true),
                    AfterHash = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpertiseAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpertiseAuditLogs_ExpertiseEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "ExpertiseEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseEntries_Tenant",
                table: "ExpertiseEntries",
                column: "Tenant");

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseEntries_Tenant_ReviewState",
                table: "ExpertiseEntries",
                columns: new[] { "Tenant", "ReviewState" })
                .Annotation("Npgsql:IndexInclude", new[] { "Id", "EntryType", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseAuditLogs_EntryId_Timestamp",
                table: "ExpertiseAuditLogs",
                columns: new[] { "EntryId", "Timestamp" })
                .Annotation("Npgsql:IndexInclude", new[] { "Action" });

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseAuditLogs_Principal_Timestamp",
                table: "ExpertiseAuditLogs",
                columns: new[] { "Principal", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpertiseAuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_ExpertiseEntries_Tenant",
                table: "ExpertiseEntries");

            migrationBuilder.DropIndex(
                name: "IX_ExpertiseEntries_Tenant_ReviewState",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "AuthorAgent",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "AuthorPrincipal",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "IntegrityHash",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "Tenant",
                table: "ExpertiseEntries");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "ExpertiseEntries");
        }
    }
}
