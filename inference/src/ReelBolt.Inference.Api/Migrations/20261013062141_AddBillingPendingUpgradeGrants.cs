using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingPendingUpgradeGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_pending_upgrade_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_subscription_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    period_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    old_entitlement_millis = table.Column<long>(type: "bigint", nullable: true),
                    new_entitlement_millis = table.Column<long>(type: "bigint", nullable: true),
                    effective_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    proration_starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    granted_millis = table.Column<long>(type: "bigint", nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    clawed_back_millis = table.Column<long>(type: "bigint", nullable: true),
                    clawed_back_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_pending_upgrade_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_billing_pending_upgrade_grants_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_pending_upgrade_grants_organization_id",
                table: "billing_pending_upgrade_grants",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_billing_pending_upgrade_grants_provider_provider_subscripti~",
                table: "billing_pending_upgrade_grants",
                columns: new[] { "provider", "provider_subscription_id" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_pending_upgrade_grants_provider_transaction_id",
                table: "billing_pending_upgrade_grants",
                columns: new[] { "provider", "transaction_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_pending_upgrade_grants");
        }
    }
}
