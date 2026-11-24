using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source_kind",
                table: "project_files",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_revision",
                table: "project_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "github_app_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_login = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    repository_selection = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    claimed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_app_installations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(100)", maxLength: 100, nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_github_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    repository_default_branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    branch = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    path_prefix = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    last_pulled_commit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_pulled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_pulled_delivery_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_pulled_file_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_git_hub_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_github_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_files_project_id_source_kind",
                table: "project_files",
                columns: new[] { "project_id", "source_kind" });

            migrationBuilder.CreateIndex(
                name: "IX_github_app_installations_installation_id",
                table: "github_app_installations",
                column: "installation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_app_installations_organization_id",
                table: "github_app_installations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_delivery_id",
                table: "github_webhook_deliveries",
                column: "delivery_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_processed_at_received_at",
                table: "github_webhook_deliveries",
                columns: new[] { "processed_at", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_github_links_organization_id",
                table: "project_github_links",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_github_links_project_id",
                table: "project_github_links",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_github_links_repository_id",
                table: "project_github_links",
                column: "repository_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_app_installations");

            migrationBuilder.DropTable(
                name: "github_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "project_github_links");

            migrationBuilder.DropIndex(
                name: "IX_project_files_project_id_source_kind",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "source_kind",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "source_revision",
                table: "project_files");
        }
    }
}
