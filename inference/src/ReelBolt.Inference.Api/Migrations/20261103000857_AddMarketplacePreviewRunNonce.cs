using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplacePreviewRunNonce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "poster_sha256",
                table: "marketplace_recipe_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preview_run_nonce",
                table: "marketplace_recipe_versions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "poster_sha256",
                table: "marketplace_recipe_versions");

            migrationBuilder.DropColumn(
                name: "preview_run_nonce",
                table: "marketplace_recipe_versions");
        }
    }
}
