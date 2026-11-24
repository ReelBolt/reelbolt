using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingRevenueDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "discount_minor",
                table: "billing_payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "earnings_minor",
                table: "billing_payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "subtotal_minor",
                table: "billing_payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tax_minor",
                table: "billing_payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_minor",
                table: "billing_payments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "billing_refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_adjustment_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    tax_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_refunds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_refunds_occurred_at",
                table: "billing_refunds",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_billing_refunds_provider_provider_adjustment_id",
                table: "billing_refunds",
                columns: new[] { "provider", "provider_adjustment_id" },
                unique: true);

            migrationBuilder.Sql(BillingRevenueDiscountMigrationSql.PowerBiGrant);

            migrationBuilder.Sql(BillingRevenueDiscountMigrationSql.BackfillPaymentTotals);

            migrationBuilder.Sql(BillingRevenueDiscountMigrationSql.BackfillRefunds);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_refunds");

            migrationBuilder.DropColumn(
                name: "discount_minor",
                table: "billing_payments");

            migrationBuilder.DropColumn(
                name: "earnings_minor",
                table: "billing_payments");

            migrationBuilder.DropColumn(
                name: "subtotal_minor",
                table: "billing_payments");

            migrationBuilder.DropColumn(
                name: "tax_minor",
                table: "billing_payments");

            migrationBuilder.DropColumn(
                name: "total_minor",
                table: "billing_payments");
        }
    }
}
