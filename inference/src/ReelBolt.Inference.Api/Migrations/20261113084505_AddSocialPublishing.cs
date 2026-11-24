using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<int>(type: "integer", nullable: false),
                    remote_account_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    display_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    avatar_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    protected_access_token = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    protected_refresh_token = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    access_token_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    granted_scopes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    connected_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_social_accounts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "social_oauth_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<int>(type: "integer", nullable: false),
                    protected_code_verifier = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    return_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_oauth_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "social_posts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    output_id = table.Column<Guid>(type: "uuid", nullable: false),
                    social_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<int>(type: "integer", nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    body = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    hashtags_json = table.Column<string>(type: "jsonb", nullable: true),
                    post_text_id = table.Column<Guid>(type: "uuid", nullable: true),
                    options_json = table.Column<string>(type: "jsonb", nullable: true),
                    scheduled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    remote_post_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    remote_post_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_posts", x => x.id);
                    table.ForeignKey(
                        name: "FK_social_posts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_social_posts_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_social_posts_social_accounts_social_account_id",
                        column: x => x.social_account_id,
                        principalTable: "social_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "social_post_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    views = table.Column<long>(type: "bigint", nullable: true),
                    likes = table.Column<long>(type: "bigint", nullable: true),
                    comments = table.Column<long>(type: "bigint", nullable: true),
                    shares = table.Column<long>(type: "bigint", nullable: true),
                    saves = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_post_metrics", x => x.id);
                    table.ForeignKey(
                        name: "FK_social_post_metrics_social_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "social_posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_social_accounts_organization_id_platform_remote_account_id",
                table: "social_accounts",
                columns: new[] { "organization_id", "platform", "remote_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_social_oauth_states_expires_at",
                table: "social_oauth_states",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_social_oauth_states_state_hash",
                table: "social_oauth_states",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_social_oauth_states_user_id",
                table: "social_oauth_states",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_social_post_metrics_post_id_captured_at",
                table: "social_post_metrics",
                columns: new[] { "post_id", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "IX_social_posts_organization_id_created_at",
                table: "social_posts",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_social_posts_project_id",
                table: "social_posts",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_social_posts_social_account_id",
                table: "social_posts",
                column: "social_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_social_posts_status_scheduled_at",
                table: "social_posts",
                columns: new[] { "status", "scheduled_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "social_oauth_states");

            migrationBuilder.DropTable(
                name: "social_post_metrics");

            migrationBuilder.DropTable(
                name: "social_posts");

            migrationBuilder.DropTable(
                name: "social_accounts");
        }
    }
}
