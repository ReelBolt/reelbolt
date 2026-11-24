using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionNotGrantedAndProviderCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "not_granted_reason",
                table: "promotion_redemptions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "promotion_redemptions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Granted");

            migrationBuilder.AddColumn<int>(
                name: "provider_cancel_attempts",
                table: "promotion_checkouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "provider_cancelled_at",
                table: "promotion_checkouts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "within_caps_at_reservation",
                table: "promotion_checkouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "not_granted_reason",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "provider_cancel_attempts",
                table: "promotion_checkouts");

            migrationBuilder.DropColumn(
                name: "provider_cancelled_at",
                table: "promotion_checkouts");

            migrationBuilder.DropColumn(
                name: "within_caps_at_reservation",
                table: "promotion_checkouts");
        }
    }
}
