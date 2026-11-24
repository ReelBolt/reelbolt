using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rate_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model_pattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    usage_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    credits_millis_per_unit = table.Column<decimal>(type: "numeric(28,10)", precision: 28, scale: 10, nullable: false),
                    cost_usd_per_unit = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_cards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_multipliers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    usage_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    multiplier = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    effective_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    effective_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_multipliers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,6)", precision: 28, scale: 6, nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    billing_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    credits_millis = table.Column<long>(type: "bigint", nullable: false),
                    cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    rate_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_usage_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_cards_provider_kind_usage_kind_model_pattern_effective~",
                table: "rate_cards",
                columns: new[] { "provider_kind", "usage_kind", "model_pattern", "effective_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rate_multipliers_billing_mode_usage_kind_effective_from",
                table: "rate_multipliers",
                columns: new[] { "billing_mode", "usage_kind", "effective_from" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_usage_events_execution_id",
                table: "usage_events",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_usage_events_idempotency_key",
                table: "usage_events",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_usage_events_organization_id_occurred_at",
                table: "usage_events",
                columns: new[] { "organization_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_cards");

            migrationBuilder.DropTable(
                name: "rate_multipliers");

            migrationBuilder.DropTable(
                name: "usage_events");
        }
    }
}
