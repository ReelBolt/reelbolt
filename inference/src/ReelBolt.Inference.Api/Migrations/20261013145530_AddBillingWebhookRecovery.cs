using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingWebhookRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "billing_webhook_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "billing_admin_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    detail_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_admin_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_reconcile_watches",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    change_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    change_requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_reconcile_watches", x => x.organization_id);
                    table.ForeignKey(
                        name: "FK_billing_reconcile_watches_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_admin_actions_organization_id_created_at",
                table: "billing_admin_actions",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_reconcile_watches_change_requested_at",
                table: "billing_reconcile_watches",
                column: "change_requested_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_admin_actions");

            migrationBuilder.DropTable(
                name: "billing_reconcile_watches");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "billing_webhook_events");
        }
    }
}
