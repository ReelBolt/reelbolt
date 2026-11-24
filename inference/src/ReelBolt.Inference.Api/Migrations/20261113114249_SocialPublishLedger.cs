using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class SocialPublishLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_publish_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<int>(type: "integer", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_publish_ledger", x => x.id);
                    table.ForeignKey(
                        name: "FK_social_publish_ledger_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_social_publish_ledger_organization_id_day",
                table: "social_publish_ledger",
                columns: new[] { "organization_id", "day" });

            migrationBuilder.CreateIndex(
                name: "IX_social_publish_ledger_platform_day",
                table: "social_publish_ledger",
                columns: new[] { "platform", "day" });

            migrationBuilder.CreateIndex(
                name: "IX_social_publish_ledger_post_id",
                table: "social_publish_ledger",
                column: "post_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "social_publish_ledger");
        }
    }
}
