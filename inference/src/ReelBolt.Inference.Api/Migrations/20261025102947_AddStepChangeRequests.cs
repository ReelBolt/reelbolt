using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStepChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "step_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_step_order = table.Column<int>(type: "integer", nullable: false),
                    target_step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    step_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    instruction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    scope_json = table.Column<string>(type: "jsonb", nullable: true),
                    overrides_json = table.Column<string>(type: "jsonb", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replay_plan_json = table.Column<string>(type: "jsonb", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_step_change_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_step_change_requests_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_step_change_requests_execution_id",
                table: "step_change_requests",
                column: "execution_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_step_change_requests_project_id_source_execution_id",
                table: "step_change_requests",
                columns: new[] { "project_id", "source_execution_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "step_change_requests");
        }
    }
}
