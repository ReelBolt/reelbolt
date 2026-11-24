using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectArchiveUnpacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_archive_unpacks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archive_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    archive_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    target_directory = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    files_created = table.Column<int>(type: "integer", nullable: false),
                    bytes_created = table.Column<long>(type: "bigint", nullable: false),
                    skipped_json = table.Column<string>(type: "jsonb", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_archive_unpacks", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_archive_unpacks_project_files_source_file_id",
                        column: x => x.source_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_archive_unpacks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_archive_unpacks_project_id_created_at",
                table: "project_archive_unpacks",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_archive_unpacks_source_file_id",
                table: "project_archive_unpacks",
                column: "source_file_id",
                unique: true,
                filter: "status IN ('Pending', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_project_archive_unpacks_status_next_attempt_at",
                table: "project_archive_unpacks",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_archive_unpacks");
        }
    }
}
