using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider_subscription_id",
                table: "promotion_redemptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "resubscribe_requested_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "promotion_checkouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bonus_promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    top_up_credits = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_checkouts", x => x.id);
                    table.ForeignKey(
                        name: "FK_promotion_checkouts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_bonus_promotion_id_organization_id",
                table: "promotion_checkouts",
                columns: new[] { "bonus_promotion_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_discount_promotion_id_organization_id",
                table: "promotion_checkouts",
                columns: new[] { "discount_promotion_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_organization_id",
                table: "promotion_checkouts",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_checkouts_provider_transaction_id",
                table: "promotion_checkouts",
                column: "provider_transaction_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_checkouts");

            migrationBuilder.DropColumn(
                name: "provider_subscription_id",
                table: "promotion_redemptions");

            migrationBuilder.DropColumn(
                name: "resubscribe_requested_at",
                table: "marketing_consents");
        }
    }
}
