using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionDiscountRemovedOnPlanChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "discount_removed_at",
                table: "promotion_redemptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discount_removed_for_price_id",
                table: "promotion_redemptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discount_removed_at",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "discount_removed_for_price_id",
                table: "promotion_redemptions");
        }
    }
}
