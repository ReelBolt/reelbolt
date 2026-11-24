using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionReservationsAndOvershoot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "overshoot",
                table: "promotion_redemptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "provider_transaction_id",
                table: "promotion_checkouts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<DateTime>(
                name: "superseded_at",
                table: "promotion_checkouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_promotion_id_provider_subscription_id",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "provider_subscription_id" },
                unique: true,
                filter: "provider_subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_promotion_id_user_id",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_bonus_promotion_id_user_id",
                table: "promotion_checkouts",
                columns: new[] { "bonus_promotion_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_discount_promotion_id_user_id",
                table: "promotion_checkouts",
                columns: new[] { "discount_promotion_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_promotion_redemptions_promotion_id_provider_subscription_id",
                table: "promotion_redemptions");

            migrationBuilder.DropIndex(
                name: "IX_promotion_redemptions_promotion_id_user_id",
                table: "promotion_redemptions");

            migrationBuilder.DropIndex(
                name: "IX_promotion_checkouts_bonus_promotion_id_user_id",
                table: "promotion_checkouts");

            migrationBuilder.DropIndex(
                name: "IX_promotion_checkouts_discount_promotion_id_user_id",
                table: "promotion_checkouts");

            migrationBuilder.DropColumn(
                name: "overshoot",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "promotion_checkouts");

            migrationBuilder.AlterColumn<string>(
                name: "provider_transaction_id",
                table: "promotion_checkouts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
