using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Content.Server.Database.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddTriadMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_price_stat",
                columns: table => new
                {
                    entity_proto = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    trade_count = table.Column<int>(type: "integer", nullable: false),
                    units = table.Column<long>(type: "bigint", nullable: false),
                    total_value = table.Column<long>(type: "bigint", nullable: false),
                    min_unit = table.Column<long>(type: "bigint", nullable: false),
                    max_unit = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_price_stat", x => new { x.entity_proto, x.currency, x.direction, x.day });
                });

            migrationBuilder.CreateTable(
                name: "market_round_participant",
                columns: table => new
                {
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    character_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_round_participant", x => new { x.round_id, x.user_id, x.character_name });
                    table.ForeignKey(
                        name: "FK_market_round_participant_round_round_id",
                        column: x => x.round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "market_transaction",
                columns: table => new
                {
                    market_transaction_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    round_id = table.Column<int>(type: "integer", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    ledger_entry_type = table.Column<string>(type: "text", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: false),
                    rail = table.Column<string>(type: "text", nullable: false),
                    gross = table.Column<long>(type: "bigint", nullable: false),
                    tax = table.Column<long>(type: "bigint", nullable: false),
                    net = table.Column<long>(type: "bigint", nullable: false),
                    list_price = table.Column<long>(type: "bigint", nullable: true),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    fail_reason = table.Column<string>(type: "text", nullable: true),
                    location_name = table.Column<string>(type: "text", nullable: true),
                    console_proto = table.Column<string>(type: "text", nullable: true),
                    market_mod = table.Column<float>(type: "real", nullable: true),
                    ship_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    calc = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_transaction", x => x.market_transaction_id);
                    table.ForeignKey(
                        name: "FK_market_transaction_player_actor_id",
                        column: x => x.actor_user_id,
                        principalTable: "player",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_market_transaction_round_round_id",
                        column: x => x.round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "sector_account_sample",
                columns: table => new
                {
                    sector_account_sample_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    round_id = table.Column<int>(type: "integer", nullable: false),
                    sampled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    account = table.Column<string>(type: "text", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sector_account_sample", x => x.sector_account_sample_id);
                    table.ForeignKey(
                        name: "FK_sector_account_sample_round_round_id",
                        column: x => x.round_id,
                        principalTable: "round",
                        principalColumn: "round_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "market_transaction_line",
                columns: table => new
                {
                    market_transaction_line_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    line_index = table.Column<int>(type: "integer", nullable: false),
                    parent_line_index = table.Column<int>(type: "integer", nullable: true),
                    entity_proto = table.Column<string>(type: "text", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<long>(type: "bigint", nullable: false),
                    line_total = table.Column<long>(type: "bigint", nullable: false),
                    multiplier = table.Column<float>(type: "real", nullable: true),
                    price_source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_transaction_line", x => x.market_transaction_line_id);
                    table.ForeignKey(
                        name: "FK_market_transaction_line_market_transaction_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "market_transaction",
                        principalColumn: "market_transaction_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "market_transaction_split",
                columns: table => new
                {
                    transaction_id = table.Column<long>(type: "bigint", nullable: false),
                    account = table.Column<string>(type: "text", nullable: false),
                    entry_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_transaction_split", x => new { x.transaction_id, x.account, x.entry_type });
                    table.ForeignKey(
                        name: "FK_market_transaction_split_market_transaction_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "market_transaction",
                        principalColumn: "market_transaction_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_actor_user_id_occurred_at",
                table: "market_transaction",
                columns: new[] { "actor_user_id", "occurred_at" },
                filter: "actor_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_kind_occurred_at",
                table: "market_transaction",
                columns: new[] { "kind", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_occurred_at",
                table: "market_transaction",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "BRIN");

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_round_id",
                table: "market_transaction",
                column: "round_id");

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_line_entity_proto_direction_occurred_at",
                table: "market_transaction_line",
                columns: new[] { "entity_proto", "direction", "occurred_at" })
                .Annotation("Npgsql:IndexInclude", new[] { "unit_price", "quantity" });

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_line_occurred_at",
                table: "market_transaction_line",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "BRIN");

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_line_transaction_id",
                table: "market_transaction_line",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "IX_market_transaction_line_transaction_id_line_index",
                table: "market_transaction_line",
                columns: new[] { "transaction_id", "line_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sector_account_sample_account_sampled_at",
                table: "sector_account_sample",
                columns: new[] { "account", "sampled_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sector_account_sample_round_id",
                table: "sector_account_sample",
                column: "round_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_price_stat");

            migrationBuilder.DropTable(
                name: "market_round_participant");

            migrationBuilder.DropTable(
                name: "market_transaction_line");

            migrationBuilder.DropTable(
                name: "market_transaction_split");

            migrationBuilder.DropTable(
                name: "sector_account_sample");

            migrationBuilder.DropTable(
                name: "market_transaction");
        }
    }
}
