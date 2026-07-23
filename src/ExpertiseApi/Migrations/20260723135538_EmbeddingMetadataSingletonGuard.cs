using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <summary>
    /// Enforces the EmbeddingMetadata singleton-row invariant at the database
    /// level (#455). The get-or-create call sites (ReembedCommand,
    /// RestoreCommand) are not atomic — two concurrent callers could both pass
    /// the null check and insert two rows. A unique index over a constant
    /// expression makes the second insert fail loudly instead. Raw SQL because
    /// EF's fluent index API cannot express a constant-expression index.
    /// </summary>
    public partial class EmbeddingMetadataSingletonGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """CREATE UNIQUE INDEX "UX_EmbeddingMetadata_Singleton" ON "EmbeddingMetadata" ((TRUE));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX "UX_EmbeddingMetadata_Singleton";""");
        }
    }
}
