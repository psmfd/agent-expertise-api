using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditCheckpointsAndSeq : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Seq",
                table: "ExpertiseAuditLogs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

            migrationBuilder.CreateTable(
                name: "AuditCheckpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    SeqFrom = table.Column<long>(type: "bigint", nullable: false),
                    SeqTo = table.Column<long>(type: "bigint", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    MerkleRoot = table.Column<string>(type: "text", nullable: false),
                    PrevCheckpointMac = table.Column<string>(type: "text", nullable: true),
                    CheckpointMac = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrityVerificationStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastResult = table.Column<string>(type: "text", nullable: false),
                    MismatchCount = table.Column<int>(type: "integer", nullable: false),
                    LegacyCount = table.Column<int>(type: "integer", nullable: false),
                    UnhashedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityVerificationStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseAuditLogs_Seq",
                table: "ExpertiseAuditLogs",
                column: "Seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditCheckpoints");

            migrationBuilder.DropTable(
                name: "IntegrityVerificationStates");

            migrationBuilder.DropIndex(
                name: "IX_ExpertiseAuditLogs_Seq",
                table: "ExpertiseAuditLogs");

            migrationBuilder.DropColumn(
                name: "Seq",
                table: "ExpertiseAuditLogs");
        }
    }
}
