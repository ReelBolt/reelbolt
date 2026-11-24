using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContentModerationDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "content_moderation_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    surface = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_length = table.Column<long>(type: "bigint", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    top_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    top_score = table.Column<double>(type: "double precision", nullable: true),
                    flagged_categories_json = table.Column<string>(type: "jsonb", nullable: false),
                    scores_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_types_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    review_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_moderation_decisions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_content_moderation_decisions_action_review_state",
                table: "content_moderation_decisions",
                columns: new[] { "action", "review_state" });

            migrationBuilder.CreateIndex(
                name: "IX_content_moderation_decisions_content_sha256",
                table: "content_moderation_decisions",
                column: "content_sha256");

            migrationBuilder.CreateIndex(
                name: "IX_content_moderation_decisions_organization_id_created_at",
                table: "content_moderation_decisions",
                columns: new[] { "organization_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "content_moderation_decisions");
        }
    }
}
