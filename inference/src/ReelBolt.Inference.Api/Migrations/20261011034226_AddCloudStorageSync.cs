using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudStorageSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_storage_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    account_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    account_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    account_display_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    protected_access_token = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    protected_refresh_token = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    access_token_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    granted_scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_storage_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cloud_storage_oauth_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    protected_code_verifier = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    return_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cloud_storage_oauth_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_cloud_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    drive_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    folder_id = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    folder_display_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    include_paths_json = table.Column<string>(type: "jsonb", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_synced_file_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_cloud_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_cloud_links_cloud_storage_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "cloud_storage_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_cloud_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cloud_storage_connections_user_id_organization_id_provider_~",
                table: "cloud_storage_connections",
                columns: new[] { "user_id", "organization_id", "provider", "account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_storage_oauth_states_expires_at",
                table: "cloud_storage_oauth_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_storage_oauth_states_state_hash",
                table: "cloud_storage_oauth_states",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_storage_oauth_states_user_id",
                table: "cloud_storage_oauth_states",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_cloud_links_connection_id",
                table: "project_cloud_links",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_cloud_links_last_synced_at",
                table: "project_cloud_links",
                column: "last_synced_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_cloud_links_project_id_provider",
                table: "project_cloud_links",
                columns: new[] { "project_id", "provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_storage_oauth_states");

            migrationBuilder.DropTable(
                name: "project_cloud_links");

            migrationBuilder.DropTable(
                name: "cloud_storage_connections");
        }
    }
}
