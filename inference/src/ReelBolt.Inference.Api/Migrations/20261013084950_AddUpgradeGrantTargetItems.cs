using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradeGrantTargetItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "charged_items",
                table: "billing_pending_upgrade_grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_items",
                table: "billing_pending_upgrade_grants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "charged_items",
                table: "billing_pending_upgrade_grants");

            migrationBuilder.DropColumn(
                name: "target_items",
                table: "billing_pending_upgrade_grants");
        }
    }
}
