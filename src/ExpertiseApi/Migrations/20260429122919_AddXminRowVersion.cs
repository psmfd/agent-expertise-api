using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ExpertiseApi.Migrations
{
    /// <summary>
    /// Registers the PostgreSQL <c>xmin</c> system column as the EF Core concurrency token
    /// on <c>ExpertiseEntries</c>. <c>xmin</c> is a system column that already exists on
    /// every Postgres table, so this migration is intentionally a no-op at the schema level
    /// — the migration exists so the model snapshot tracks the EF-side mapping. Generated
    /// AddColumn / DropColumn calls were removed because they would attempt to create a
    /// duplicate user-defined column or drop a system column.
    /// </summary>
    public partial class AddXminRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op — xmin already exists as a Postgres system column.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op — xmin cannot be dropped (it's a system column).
        }
    }
}
