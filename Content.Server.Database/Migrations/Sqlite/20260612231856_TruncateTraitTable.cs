using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class TruncateTraitTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Real Traits reworks every trait id, so stored selections no longer resolve. Clear them.
            // SQLite has no TRUNCATE; delete rows and reset the autoincrement counter.
            migrationBuilder.Sql(@"
                DELETE FROM trait;
                DELETE FROM sqlite_sequence WHERE name='trait';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deletion cannot be reversed.
        }
    }
}
