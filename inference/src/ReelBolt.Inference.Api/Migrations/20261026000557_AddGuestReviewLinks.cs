using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestReviewLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "guest_link_id",
                table: "edit_timeline_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "guest_name",
                table: "edit_timeline_comments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "edit_review_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    render_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_revision = table.Column<int>(type: "integer", nullable: false),
                    allow_comments = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_viewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    view_count = table.Column<int>(type: "integer", nullable: false),
                    owner_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_notified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_review_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_review_links_edit_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "edit_timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_edit_review_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "edit_review_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    guest_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_review_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_edit_review_decisions_edit_review_links_link_id",
                        column: x => x.link_id,
                        principalTable: "edit_review_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_edit_timeline_comments_guest_link_id",
                table: "edit_timeline_comments",
                column: "guest_link_id");

            migrationBuilder.CreateIndex(
                name: "IX_edit_review_decisions_link_id_created_at",
                table: "edit_review_decisions",
                columns: new[] { "link_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_edit_review_links_project_id",
                table: "edit_review_links",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_edit_review_links_timeline_id_created_at",
                table: "edit_review_links",
                columns: new[] { "timeline_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_edit_review_links_token_hash",
                table: "edit_review_links",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_review_decisions");

            migrationBuilder.DropTable(
                name: "edit_review_links");

            migrationBuilder.DropIndex(
                name: "IX_edit_timeline_comments_guest_link_id",
                table: "edit_timeline_comments");

            migrationBuilder.DropColumn(
                name: "guest_link_id",
                table: "edit_timeline_comments");

            migrationBuilder.DropColumn(
                name: "guest_name",
                table: "edit_timeline_comments");
        }
    }
}
