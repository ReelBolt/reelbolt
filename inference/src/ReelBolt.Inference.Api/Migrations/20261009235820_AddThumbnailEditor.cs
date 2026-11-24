using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddThumbnailEditor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "blend_mode",
                table: "edit_image_layers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Hand-edited from the generated "" / false: these defaults exist to BACKFILL the layers
                // written before this migration (a visible, unlocked, normal-blend bitmap layer is what
                // they have always been). The model deliberately configures no store default - see
                // InferenceApiDbContext - so every new row supplies its own values.
                defaultValue: "normal");

            migrationBuilder.AddColumn<string>(
                name: "content_json",
                table: "edit_image_layers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "locked",
                table: "edit_image_layers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "provenance_json",
                table: "edit_image_layers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "visible",
                table: "edit_image_layers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "background_color",
                table: "edit_image_documents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "canvas_height",
                table: "edit_image_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "canvas_width",
                table: "edit_image_documents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "edit_image_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_image_exports", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_image_exports_edit_image_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "edit_image_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edit_image_exports_project_files_project_file_id",
                        column: x => x.project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_exports_document_id_created_at",
                table: "edit_image_exports",
                columns: new[] { "document_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_exports_project_file_id",
                table: "edit_image_exports",
                column: "project_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_image_exports");

            migrationBuilder.DropColumn(
                name: "blend_mode",
                table: "edit_image_layers");

            migrationBuilder.DropColumn(
                name: "content_json",
                table: "edit_image_layers");

            migrationBuilder.DropColumn(
                name: "locked",
                table: "edit_image_layers");

            migrationBuilder.DropColumn(
                name: "provenance_json",
                table: "edit_image_layers");

            migrationBuilder.DropColumn(
                name: "visible",
                table: "edit_image_layers");

            migrationBuilder.DropColumn(
                name: "background_color",
                table: "edit_image_documents");

            migrationBuilder.DropColumn(
                name: "canvas_height",
                table: "edit_image_documents");

            migrationBuilder.DropColumn(
                name: "canvas_width",
                table: "edit_image_documents");
        }
    }
}
