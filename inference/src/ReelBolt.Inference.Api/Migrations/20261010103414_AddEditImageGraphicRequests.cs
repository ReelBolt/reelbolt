using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEditImageGraphicRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edit_image_graphic_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    placement = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    frame_width = table.Column<int>(type: "integer", nullable: false),
                    frame_height = table.Column<int>(type: "integer", nullable: false),
                    sized = table.Column<bool>(type: "boolean", nullable: false),
                    frame_x = table.Column<double>(type: "double precision", nullable: false),
                    frame_y = table.Column<double>(type: "double precision", nullable: false),
                    frame_scale = table.Column<double>(type: "double precision", nullable: false),
                    target_x = table.Column<double>(type: "double precision", nullable: false),
                    target_y = table.Column<double>(type: "double precision", nullable: false),
                    target_width = table.Column<double>(type: "double precision", nullable: false),
                    target_height = table.Column<double>(type: "double precision", nullable: false),
                    revises_layer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    result_storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    result_width = table.Column<int>(type: "integer", nullable: true),
                    result_height = table.Column<int>(type: "integer", nullable: true),
                    result_offset_x = table.Column<int>(type: "integer", nullable: true),
                    result_offset_y = table.Column<int>(type: "integer", nullable: true),
                    result_workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    result_version_number = table.Column<int>(type: "integer", nullable: true),
                    result_composition_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    result_model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    result_layer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_image_graphic_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_image_graphic_requests_edit_image_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "edit_image_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edit_image_graphic_requests_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_graphic_requests_document_id",
                table: "edit_image_graphic_requests",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_graphic_requests_execution_id",
                table: "edit_image_graphic_requests",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_edit_image_graphic_requests_project_id_requested_by_user_id~",
                table: "edit_image_graphic_requests",
                columns: new[] { "project_id", "requested_by_user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_image_graphic_requests");
        }
    }
}
