using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEditTimelineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "edit_timelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    seed_step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_timelines", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_timelines_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_derivatives",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_project_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    storage_bucket = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_sec = table.Column<double>(type: "double precision", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_derivatives", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_derivatives_project_files_source_project_file_id",
                        column: x => x.source_project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_media_derivatives_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "edit_timeline_ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    ops_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_timeline_ops", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_timeline_ops_edit_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "edit_timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "edit_timeline_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    document_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_timeline_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_timeline_snapshots_edit_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "edit_timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edit_timeline_ops_timeline_id_revision",
                table: "edit_timeline_ops",
                columns: new[] { "timeline_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_edit_timeline_snapshots_timeline_id_revision",
                table: "edit_timeline_snapshots",
                columns: new[] { "timeline_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_edit_timelines_project_id",
                table: "edit_timelines",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_derivatives_project_id",
                table: "media_derivatives",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_derivatives_source_project_file_id",
                table: "media_derivatives",
                column: "source_project_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_timeline_ops");

            migrationBuilder.DropTable(
                name: "edit_timeline_snapshots");

            migrationBuilder.DropTable(
                name: "media_derivatives");

            migrationBuilder.DropTable(
                name: "edit_timelines");
        }
    }
}
