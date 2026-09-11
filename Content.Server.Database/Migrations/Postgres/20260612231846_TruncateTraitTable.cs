using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class TruncateTraitTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Real Traits reworks every trait id, so stored selections no longer resolve. Clear them.
            migrationBuilder.Sql("TRUNCATE TABLE trait RESTART IDENTITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Truncation cannot be reversed.
        }
    }
}
