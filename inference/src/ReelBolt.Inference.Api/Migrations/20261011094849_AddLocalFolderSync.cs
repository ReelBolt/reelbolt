using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalFolderSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_link_id",
                table: "project_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_local_folder_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_folder = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paused_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    pause_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_reconciled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_change_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_local_folder_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_local_folder_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_local_folder_links_runner_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "runner_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_files_source_link_id",
                table: "project_files",
                column: "source_link_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_local_folder_links_device_id",
                table: "project_local_folder_links",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_local_folder_links_project_id_target_folder",
                table: "project_local_folder_links",
                columns: new[] { "project_id", "target_folder" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_project_files_project_local_folder_links_source_link_id",
                table: "project_files",
                column: "source_link_id",
                principalTable: "project_local_folder_links",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_project_files_project_local_folder_links_source_link_id",
                table: "project_files");

            migrationBuilder.DropTable(
                name: "project_local_folder_links");

            migrationBuilder.DropIndex(
                name: "IX_project_files_source_link_id",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "source_link_id",
                table: "project_files");
        }
    }
}
