using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Editor v2, P9: per-user editor preferences (<c>editor_user_preferences</c> — keymap, saved
    /// layouts), project presets (<c>editor_presets</c> — transition/effect/mastering) and timecoded
    /// review comments (<c>edit_timeline_comments</c>). Hand-written (the <c>dotnet-ef</c> tool cannot
    /// start on this host); the model snapshot was updated by hand to match and
    /// <c>InferenceApiMigrationSnapshotTests</c> proves there is no pending model change.
    /// </summary>
    public partial class AddEditorP9Tables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "editor_user_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    keymap_json = table.Column<string>(type: "jsonb", nullable: false),
                    layouts_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_editor_user_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_editor_user_preferences_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "editor_presets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_editor_presets", x => x.id);
                    table.ForeignKey(
                        name: "fk_editor_presets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_editor_presets_project_id_kind",
                table: "editor_presets",
                columns: new[] { "project_id", "kind" });

            migrationBuilder.CreateTable(
                name: "edit_timeline_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    frame = table.Column<long>(type: "bigint", nullable: false),
                    duration_frames = table.Column<long>(type: "bigint", nullable: true),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    resolved = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_timeline_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_edit_timeline_comments_edit_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "edit_timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edit_timeline_comments_project_id",
                table: "edit_timeline_comments",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_edit_timeline_comments_timeline_id_frame",
                table: "edit_timeline_comments",
                columns: new[] { "timeline_id", "frame" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "edit_timeline_comments");
            migrationBuilder.DropTable(name: "editor_presets");
            migrationBuilder.DropTable(name: "editor_user_preferences");
        }
    }
}
