using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductEventsFirstPaymentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_product_events_first_payment_organization",
                table: "product_events",
                columns: new[] { "organization_id", "event" },
                unique: true,
                filter: "event = 'alpha.first_payment'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_product_events_first_payment_organization",
                table: "product_events");
        }
    }
}
