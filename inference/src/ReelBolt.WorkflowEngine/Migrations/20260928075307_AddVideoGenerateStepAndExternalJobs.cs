using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoGenerateStepAndExternalJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "video_generate_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "external_generation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clip_id = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remote_job_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    cost_estimate_usd = table.Column<decimal>(type: "numeric", nullable: false),
                    actual_cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    storage_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_generation_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_generation_jobs_project_id_created_at",
                table: "external_generation_jobs",
                columns: new[] { "project_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_generation_jobs");

            migrationBuilder.DropColumn(
                name: "video_generate_config_json",
                table: "workflow_steps");
        }
    }
}
