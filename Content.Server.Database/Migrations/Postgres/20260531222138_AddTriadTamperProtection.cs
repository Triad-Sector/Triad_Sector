using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTriadTamperProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "triad_shipyard_audit_events",
                columns: table => new
                {
                    triad_shipyard_audit_events_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    server_name = table.Column<string>(type: "text", nullable: true),
                    player_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    player_name = table.Column<string>(type: "text", nullable: true),
                    ship_name = table.Column<string>(type: "text", nullable: true),
                    ship_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    signing_key_id = table.Column<int>(type: "integer", nullable: true),
                    save_time_appraisal = table.Column<int>(type: "integer", nullable: true),
                    load_time_appraisal = table.Column<int>(type: "integer", nullable: true),
                    vessel_id = table.Column<string>(type: "text", nullable: true),
                    map_id = table.Column<string>(type: "text", nullable: true),
                    source_file_path = table.Column<string>(type: "text", nullable: true),
                    deed_holder_entity = table.Column<string>(type: "text", nullable: true),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triad_shipyard_audit_events", x => x.triad_shipyard_audit_events_id);
                });

            migrationBuilder.CreateTable(
                name: "triad_shipyard_migration_permits",
                columns: table => new
                {
                    triad_shipyard_migration_permits_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    player_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_by_admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triad_shipyard_migration_permits", x => x.triad_shipyard_migration_permits_id);
                });

            migrationBuilder.CreateTable(
                name: "triad_shipyard_signing_keys",
                columns: table => new
                {
                    triad_shipyard_signing_keys_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triad_shipyard_signing_keys", x => x.triad_shipyard_signing_keys_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_audit_events_event_type_at",
                table: "triad_shipyard_audit_events",
                columns: new[] { "event_type", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_audit_events_player_user_id_at",
                table: "triad_shipyard_audit_events",
                columns: new[] { "player_user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_audit_events_ship_hash_at",
                table: "triad_shipyard_audit_events",
                columns: new[] { "ship_hash", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_migration_permits_player_user_id",
                table: "triad_shipyard_migration_permits",
                column: "player_user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "triad_shipyard_audit_events");

            migrationBuilder.DropTable(
                name: "triad_shipyard_migration_permits");

            migrationBuilder.DropTable(
                name: "triad_shipyard_signing_keys");
        }
    }
}
