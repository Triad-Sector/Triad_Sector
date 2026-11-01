using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTriadDrydock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drydock_audit",
                columns: table => new
                {
                    drydock_audit_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    berth_id = table.Column<int>(type: "integer", nullable: true),
                    ship_name = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subject_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: true),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_audit", x => x.drydock_audit_id);
                });

            migrationBuilder.CreateTable(
                name: "drydock_berth",
                columns: table => new
                {
                    berth_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_size_class = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    price_paid = table.Column<int>(type: "integer", nullable: false),
                    purchased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    purchased_round_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_berth", x => x.berth_id);
                    table.UniqueConstraint("ak_drydock_berth_berth_id_owner_user_id", x => new { x.berth_id, x.owner_user_id });
                    table.ForeignKey(
                        name: "FK_drydock_berth_player_owner_id",
                        column: x => x.owner_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drydock_berth_round_purchased_round_id",
                        column: x => x.purchased_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "triad_shipyard_consumed_ships",
                columns: table => new
                {
                    triad_shipyard_consumed_ships_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ship_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    player_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    imported_round_id = table.Column<int>(type: "integer", nullable: true),
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    ship_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_triad_shipyard_consumed_ships", x => x.triad_shipyard_consumed_ships_id);
                    table.ForeignKey(
                        name: "FK_triad_shipyard_consumed_ships_round_imported_round_id",
                        column: x => x.imported_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "drydock_ship",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ship_name = table.Column<string>(type: "text", nullable: false),
                    captain_name = table.Column<string>(type: "text", nullable: true),
                    vessel_proto = table.Column<string>(type: "text", nullable: true),
                    size_class = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    state_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    checked_out_round_id = table.Column<int>(type: "integer", nullable: true),
                    admin_notes = table.Column<string>(type: "text", nullable: true),
                    impound_fee = table.Column<int>(type: "integer", nullable: false),
                    impound_reason = table.Column<string>(type: "text", nullable: true),
                    impound_redeemable = table.Column<bool>(type: "boolean", nullable: false),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    berth_id = table.Column<int>(type: "integer", nullable: true),
                    last_berth_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_ship", x => x.ship_guid);
                    table.ForeignKey(
                        name: "FK_drydock_ship_drydock_berth_berth_id",
                        columns: x => new { x.berth_id, x.owner_user_id },
                        principalTable: "drydock_berth",
                        principalColumns: new[] { "berth_id", "owner_user_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drydock_ship_drydock_berth_last_berth_id",
                        column: x => x.last_berth_id,
                        principalTable: "drydock_berth",
                        principalColumn: "berth_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_drydock_ship_player_owner_id",
                        column: x => x.owner_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drydock_ship_round_checked_out_round_id",
                        column: x => x.checked_out_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "drydock_revision",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    derived_from_revision = table.Column<int>(type: "integer", nullable: true),
                    rebake_version = table.Column<int>(type: "integer", nullable: false),
                    pinned = table.Column<bool>(type: "boolean", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_round_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    engine_format_ver = table.Column<int>(type: "integer", nullable: false),
                    drydock_format_ver = table.Column<int>(type: "integer", nullable: false),
                    proto_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    captured_key_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    checksum = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<int>(type: "integer", nullable: false),
                    appraised_value = table.Column<int>(type: "integer", nullable: true),
                    manifest = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_revision", x => new { x.ship_guid, x.revision });
                    table.ForeignKey(
                        name: "FK_drydock_revision_drydock_ship_ship_temp_id",
                        column: x => x.ship_guid,
                        principalTable: "drydock_ship",
                        principalColumn: "ship_guid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_drydock_revision_player_actor_id",
                        column: x => x.actor_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_drydock_revision_round_created_round_id",
                        column: x => x.created_round_id,
                        principalTable: "round",
                        principalColumn: "round_id");
                });

            migrationBuilder.CreateTable(
                name: "drydock_transfer",
                columns: table => new
                {
                    drydock_transfer_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolution = table.Column<int>(type: "integer", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    round_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_transfer", x => x.drydock_transfer_id);
                    table.ForeignKey(
                        name: "FK_drydock_transfer_drydock_ship_ship_temp_id1",
                        column: x => x.ship_guid,
                        principalTable: "drydock_ship",
                        principalColumn: "ship_guid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "drydock_blob",
                columns: table => new
                {
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    blob = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drydock_blob", x => new { x.ship_guid, x.revision });
                    table.ForeignKey(
                        name: "FK_drydock_blob_drydock_revision_revision_row_temp_id",
                        columns: x => new { x.ship_guid, x.revision },
                        principalTable: "drydock_revision",
                        principalColumns: new[] { "ship_guid", "revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_actor_user_id",
                table: "drydock_audit",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_ship_guid_created_at",
                table: "drydock_audit",
                columns: new[] { "ship_guid", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_audit_subject_user_id",
                table: "drydock_audit",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_berth_owner_user_id",
                table: "drydock_berth",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_berth_purchased_round_id",
                table: "drydock_berth",
                column: "purchased_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_revision_actor_user_id",
                table: "drydock_revision",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_revision_created_round_id",
                table: "drydock_revision",
                column: "created_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_berth_id",
                table: "drydock_ship",
                column: "berth_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_berth_id_owner_user_id",
                table: "drydock_ship",
                columns: new[] { "berth_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_checked_out_round_id",
                table: "drydock_ship",
                column: "checked_out_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_last_berth_id",
                table: "drydock_ship",
                column: "last_berth_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_owner_user_id",
                table: "drydock_ship",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_ship_state_state_changed_at",
                table: "drydock_ship",
                columns: new[] { "state", "state_changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_resolution_expires_at",
                table: "drydock_transfer",
                columns: new[] { "resolution", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_ship_guid",
                table: "drydock_transfer",
                column: "ship_guid",
                unique: true,
                filter: "resolution = 0");

            migrationBuilder.CreateIndex(
                name: "IX_drydock_transfer_to_user_id_resolution",
                table: "drydock_transfer",
                columns: new[] { "to_user_id", "resolution" });

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_imported_round_id",
                table: "triad_shipyard_consumed_ships",
                column: "imported_round_id");

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_player_user_id",
                table: "triad_shipyard_consumed_ships",
                column: "player_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_triad_shipyard_consumed_ships_ship_hash",
                table: "triad_shipyard_consumed_ships",
                column: "ship_hash",
                unique: true);

            // Triad: the blob arrives already compressed by the game server, so tell TOAST
            // to store it out of line and not to spend CPU trying to compress it again. No
            // EF expression covers column storage, so this is hand-written and has to be
            // re-added by hand if this migration is ever regenerated.
            migrationBuilder.Sql("ALTER TABLE drydock_blob ALTER COLUMN blob SET STORAGE EXTERNAL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drydock_audit");

            migrationBuilder.DropTable(
                name: "drydock_blob");

            migrationBuilder.DropTable(
                name: "drydock_transfer");

            migrationBuilder.DropTable(
                name: "triad_shipyard_consumed_ships");

            migrationBuilder.DropTable(
                name: "drydock_revision");

            migrationBuilder.DropTable(
                name: "drydock_ship");

            migrationBuilder.DropTable(
                name: "drydock_berth");
        }
    }
}
