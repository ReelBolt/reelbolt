using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEditImageDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edit_image_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_project_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_image_documents", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_image_documents_project_files_source_project_file_id",
                        column: x => x.source_project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edit_image_documents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "edit_image_layers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    zorder = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    source_project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    storage_bucket = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    matte_storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    opacity = table.Column<double>(type: "double precision", nullable: false),
                    transform_json = table.Column<string>(type: "jsonb", nullable: false),
                    segmentation_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    segmentation_prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_image_layers", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_image_layers_project_files_source_project_file_id",
                        column: x => x.source_project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_edit_image_layers_edit_image_documents_edit_image_document_~",
                        column: x => x.document_id,
                        principalTable: "edit_image_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_documents_project_id",
                table: "edit_image_documents",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_documents_source_project_file_id",
                table: "edit_image_documents",
                column: "source_project_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_layers_document_id_zorder",
                table: "edit_image_layers",
                columns: new[] { "document_id", "zorder" });

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_layers_source_project_file_id",
                table: "edit_image_layers",
                column: "source_project_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_image_layers");

            migrationBuilder.DropTable(
                name: "edit_image_documents");
        }
    }
}
