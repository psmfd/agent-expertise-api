using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <summary>
    /// ADR-017 model swap: bge-micro-v2 (384-dim) → jina-embeddings-v2-small-en
    /// (512-dim). pgvector's typmod cast rejects ALTER COLUMN TYPE over live
    /// vectors of a different dimension, and the old vectors are garbage in the
    /// new space anyway, so the migration nulls them first. Every read path
    /// filters Embedding != null, so the window until `reembed` repopulates is
    /// a soft degradation of semantic search, not an outage. The HNSW index is
    /// dropped before the retype and recreated after — NULLs are not indexed,
    /// so the recreate is instant and fills incrementally as reembed writes.
    ///
    /// FORWARD-ONLY in practice: Down() restores the column type but cannot
    /// restore the destroyed 384-dim vectors (regenerable via the old model's
    /// `reembed` only). See ADR-017's rollback section.
    /// </summary>
    public partial class SwapEmbeddingModelTo512Dim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpertiseEntries_Embedding",
                table: "ExpertiseEntries");

            migrationBuilder.Sql(
                """UPDATE "ExpertiseEntries" SET "Embedding" = NULL;""");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "ExpertiseEntries",
                type: "vector(512)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(384)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseEntries_Embedding",
                table: "ExpertiseEntries",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExpertiseEntries_Embedding",
                table: "ExpertiseEntries");

            // 512-dim vectors cannot survive a retype to vector(384) either.
            migrationBuilder.Sql(
                """UPDATE "ExpertiseEntries" SET "Embedding" = NULL;""");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "ExpertiseEntries",
                type: "vector(384)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(512)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpertiseEntries_Embedding",
                table: "ExpertiseEntries",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }
    }
}
