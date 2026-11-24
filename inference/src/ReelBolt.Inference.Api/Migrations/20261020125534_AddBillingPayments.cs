using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_subscription_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    fee_minor = table.Column<long>(type: "bigint", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_payments", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_occurred_at",
                table: "billing_payments",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_organization_id",
                table: "billing_payments",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_billing_payments_provider_provider_transaction_id",
                table: "billing_payments",
                columns: new[] { "provider", "provider_transaction_id" },
                unique: true);

            migrationBuilder.Sql(BillingPaymentsMigrationSql.PowerBiGrant);

            migrationBuilder.Sql(BillingPaymentsMigrationSql.Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_payments");
        }
    }
}
