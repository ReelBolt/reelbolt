using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignSystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "design_system_change_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    drift_score = table.Column<double>(type: "double precision", nullable: false),
                    diff_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    proposed_digest = table.Column<string>(type: "char(64)", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_change_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_system_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    importer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    source_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    last_checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_sources", x => x.id);
                    table.ForeignKey(
                        name: "FK_design_system_sources_project_files_project_file_id",
                        column: x => x.project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "design_system_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    document_json = table.Column<string>(type: "jsonb", nullable: false),
                    document_digest = table.Column<string>(type: "char(64)", nullable: false),
                    origin = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    base_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    workflow_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coverage_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "design_systems",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    watch_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_systems", x => x.id);
                    table.CheckConstraint("ck_design_systems_kind", "kind IN ('design','product_map')");
                    table.ForeignKey(
                        name: "FK_design_systems_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_change_proposals_design_system_id_proposed_di~",
                table: "design_system_change_proposals",
                columns: new[] { "design_system_id", "proposed_digest" });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_change_proposals_design_system_id_status",
                table: "design_system_change_proposals",
                columns: new[] { "design_system_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_sources_design_system_id_path",
                table: "design_system_sources",
                columns: new[] { "design_system_id", "path" });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_sources_project_file_id",
                table: "design_system_sources",
                column: "project_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_system_sources_version_id",
                table: "design_system_sources",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_system_versions_design_system_id_state",
                table: "design_system_versions",
                columns: new[] { "design_system_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_versions_design_system_id_version_number",
                table: "design_system_versions",
                columns: new[] { "design_system_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_design_systems_current_version_id",
                table: "design_systems",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_systems_project_id",
                table: "design_systems",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_systems_project_id_kind",
                table: "design_systems",
                columns: new[] { "project_id", "kind" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_design_system_change_proposals_design_systems_design_system~",
                table: "design_system_change_proposals",
                column: "design_system_id",
                principalTable: "design_systems",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_design_system_sources_design_system_versions_version_id",
                table: "design_system_sources",
                column: "version_id",
                principalTable: "design_system_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_design_system_sources_design_systems_design_system_id",
                table: "design_system_sources",
                column: "design_system_id",
                principalTable: "design_systems",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_design_system_versions_design_systems_design_system_id",
                table: "design_system_versions",
                column: "design_system_id",
                principalTable: "design_systems",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            // The circular half: design_systems.current_version_id -> design_system_versions(id)
            // can only be added once design_system_versions exists, so it is not in its CreateTable.
            migrationBuilder.AddForeignKey(
                name: "FK_design_systems_design_system_versions_current_version_id",
                table: "design_systems",
                column: "current_version_id",
                principalTable: "design_system_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_design_systems_design_system_versions_current_version_id",
                table: "design_systems");

            migrationBuilder.DropForeignKey(
                name: "FK_design_system_versions_design_systems_design_system_id",
                table: "design_system_versions");

            migrationBuilder.DropTable(
                name: "design_system_change_proposals");

            migrationBuilder.DropTable(
                name: "design_system_sources");

            migrationBuilder.DropTable(
                name: "design_systems");

            migrationBuilder.DropTable(
                name: "design_system_versions");
        }
    }
}
