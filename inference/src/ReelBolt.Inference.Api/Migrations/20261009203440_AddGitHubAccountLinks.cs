using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAccountLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_account_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_user_id = table.Column<long>(type: "bigint", nullable: false),
                    login = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    avatar_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    protected_access_token = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    protected_refresh_token = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    access_token_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    refresh_token_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    connected_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_account_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "github_oauth_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    binding_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    protected_code_verifier = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_git_hub_oauth_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_account_links_github_user_id",
                table: "github_account_links",
                column: "github_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_account_links_user_id_organization_id",
                table: "github_account_links",
                columns: new[] { "user_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_oauth_states_expires_at",
                table: "github_oauth_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_github_oauth_states_state_hash",
                table: "github_oauth_states",
                column: "state_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_account_links");

            migrationBuilder.DropTable(
                name: "github_oauth_states");
        }
    }
}
