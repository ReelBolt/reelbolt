using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectEffects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "object_effects_consent_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "object_effect_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prompt = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    match_scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    effect = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    strength = table.Column<double>(type: "double precision", nullable: false),
                    invert = table.Column<bool>(type: "boolean", nullable: false),
                    feather_px = table.Column<int>(type: "integer", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timeline_item_ids_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finalized_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    applied_revision = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_object_effect_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_object_effect_requests_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_object_effect_requests_execution_id",
                table: "object_effect_requests",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_object_effect_requests_project_id_source_id_prompt",
                table: "object_effect_requests",
                columns: new[] { "project_id", "source_id", "prompt" });

            // An EXPRESSION index (the model cannot declare it): one in-flight analysis per identical (source, prompt, scope, timeline
            // intent). coalesce makes a null timeline (the editor path) conflict with another null timeline; partial, so finished rows never block.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_object_effect_requests_inflight ON object_effect_requests " +
                "(project_id, source_kind, source_id, prompt, match_scope, coalesce(timeline_id, '00000000-0000-0000-0000-000000000000'::uuid)) " +
                "WHERE status = 'queued';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "object_effect_requests");

            migrationBuilder.DropColumn(
                name: "object_effects_consent_at",
                table: "projects");
        }
    }
}
