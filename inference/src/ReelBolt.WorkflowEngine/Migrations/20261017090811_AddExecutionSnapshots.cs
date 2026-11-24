using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "superseded_at",
                table: "workflow_step_results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "agent_snapshot_json",
                table: "workflow_executions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "auto_resume_count",
                table: "workflow_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "pending_resume",
                table: "workflow_executions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "resume_count",
                table: "workflow_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "snapshot_definition_id",
                table: "workflow_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "snapshot_of_execution_id",
                table: "workflow_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_executions_snapshot_definition_id",
                table: "workflow_executions",
                column: "snapshot_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_snapshot_of_execution_id",
                table: "workflow_definitions",
                column: "snapshot_of_execution_id");

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_definitions_workflow_executions_snapshot_of_execut~",
                table: "workflow_definitions",
                column: "snapshot_of_execution_id",
                principalTable: "workflow_executions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_executions_workflow_definitions_snapshot_definitio~",
                table: "workflow_executions",
                column: "snapshot_definition_id",
                principalTable: "workflow_definitions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_definitions_workflow_executions_snapshot_of_execut~",
                table: "workflow_definitions");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_executions_workflow_definitions_snapshot_definitio~",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_executions_snapshot_definition_id",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_definitions_snapshot_of_execution_id",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "agent_snapshot_json",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "auto_resume_count",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "pending_resume",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "resume_count",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "snapshot_definition_id",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "snapshot_of_execution_id",
                table: "workflow_definitions");
        }
    }
}
