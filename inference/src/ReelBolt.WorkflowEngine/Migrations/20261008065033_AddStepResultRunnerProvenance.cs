using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddStepResultRunnerProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "compute_target",
                table: "workflow_step_results",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "from_runner",
                table: "workflow_step_results",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "runner_id",
                table: "workflow_step_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "runner_job_id",
                table: "workflow_step_results",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compute_target",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "from_runner",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "runner_id",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "runner_job_id",
                table: "workflow_step_results");
        }
    }
}
