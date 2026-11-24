using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_sha256",
                table: "project_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "content_sha256_etag",
                table: "project_files",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_history_blobs",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_history_blobs", x => new { x.project_id, x.content_id });
                    table.ForeignKey(
                        name: "FK_project_history_blobs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_history_documents",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    json = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_history_documents", x => new { x.project_id, x.sha256 });
                    table.ForeignKey(
                        name: "FK_project_history_documents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    cause = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    manifest_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    item_count = table.Column<int>(type: "integer", nullable: false),
                    total_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_versions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_version_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    content_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    storage_etag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    output_produced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    platform_made = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    document_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    timeline_revision = table.Column<int>(type: "integer", nullable: true),
                    design_system_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    erased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_version_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_version_items_project_versions_project_version_id",
                        column: x => x.version_id,
                        principalTable: "project_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_version_items_project_id_storage_key",
                table: "project_version_items",
                columns: new[] { "project_id", "storage_key" });

            migrationBuilder.CreateIndex(
                name: "IX_project_version_items_version_id_kind_item_key",
                table: "project_version_items",
                columns: new[] { "version_id", "kind", "item_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_versions_project_id_created_at",
                table: "project_versions",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_versions_project_id_number",
                table: "project_versions",
                columns: new[] { "project_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_history_blobs");

            migrationBuilder.DropTable(
                name: "project_history_documents");

            migrationBuilder.DropTable(
                name: "project_version_items");

            migrationBuilder.DropTable(
                name: "project_versions");

            migrationBuilder.DropColumn(
                name: "content_sha256",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "content_sha256_etag",
                table: "project_files");
        }
    }
}
