using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// contract-migration(UniqueIndex): expand only. The one unique index added to an existing table is ux_avatar_library_items_variant_live_label on (variant_of_item_id, variant_label), filtered to variant_of_item_id IS NOT NULL, and both columns are nullable columns this same migration adds: every existing row is NULL there (outside the filter), no released writer sets them, so the old release cannot violate it. The foreign key is on that same new nullable column.

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarLibraryVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "variant_description",
                table: "avatar_library_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "variant_label",
                table: "avatar_library_items",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "variant_of_item_id",
                table: "avatar_library_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_avatar_library_items_variant_live_label",
                table: "avatar_library_items",
                columns: new[] { "variant_of_item_id", "variant_label" },
                unique: true,
                filter: "variant_of_item_id IS NOT NULL AND consent_revoked_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_avatar_library_items_variant_of_item",
                table: "avatar_library_items",
                column: "variant_of_item_id",
                principalTable: "avatar_library_items",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_avatar_library_items_variant_of_item",
                table: "avatar_library_items");

            migrationBuilder.DropIndex(
                name: "ux_avatar_library_items_variant_live_label",
                table: "avatar_library_items");

            migrationBuilder.DropColumn(
                name: "variant_description",
                table: "avatar_library_items");

            migrationBuilder.DropColumn(
                name: "variant_label",
                table: "avatar_library_items");

            migrationBuilder.DropColumn(
                name: "variant_of_item_id",
                table: "avatar_library_items");
        }
    }
}
