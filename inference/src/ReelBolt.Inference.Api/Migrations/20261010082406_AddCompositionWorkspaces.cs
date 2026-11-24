using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositionWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "composition_workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin_workflow_definition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    latest_version_number = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_composition_workspaces", x => x.id);
                    table.ForeignKey(
                        name: "FK_composition_workspaces_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "composition_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instruction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    targets_json = table.Column<string>(type: "jsonb", nullable: true),
                    mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_composition_revisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_composition_revisions_composition_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "composition_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_composition_revisions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "composition_workspace_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    parent_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    composition_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fps = table.Column<double>(type: "double precision", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    duration_in_frames = table.Column<int>(type: "integer", nullable: true),
                    scene_manifest_json = table.Column<string>(type: "jsonb", nullable: true),
                    render_output_storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    instruction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_composition_workspace_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_composition_workspace_versions_composition_workspaces_works~",
                        column: x => x.workspace_id,
                        principalTable: "composition_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_composition_revisions_execution_id",
                table: "composition_revisions",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_composition_revisions_project_id_requested_by_user_id_creat~",
                table: "composition_revisions",
                columns: new[] { "project_id", "requested_by_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_composition_revisions_workspace_id",
                table: "composition_revisions",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_composition_workspace_versions_created_by_execution_id",
                table: "composition_workspace_versions",
                column: "created_by_execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_composition_workspace_versions_project_id",
                table: "composition_workspace_versions",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_composition_workspace_versions_workspace_id_version_number",
                table: "composition_workspace_versions",
                columns: new[] { "workspace_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_composition_workspaces_origin_execution_id",
                table: "composition_workspaces",
                column: "origin_execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_composition_workspaces_project_id",
                table: "composition_workspaces",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "composition_revisions");

            migrationBuilder.DropTable(
                name: "composition_workspace_versions");

            migrationBuilder.DropTable(
                name: "composition_workspaces");
        }
    }
}
