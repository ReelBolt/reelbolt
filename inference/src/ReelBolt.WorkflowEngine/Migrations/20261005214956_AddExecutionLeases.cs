using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "heartbeat_at",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_expires_at",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_owner",
                table: "workflow_executions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "queued_at",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_executions_lease_expires_at",
                table: "workflow_executions",
                column: "lease_expires_at",
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_executions_queued_at",
                table: "workflow_executions",
                column: "queued_at",
                filter: "status = 'Queued'");

            // Rows already queued age from this deploy, so the sweeper never re-publishes a request
            // that is merely waiting for its turn. Running rows keep a NULL lease: they were started by
            // a pre-lease engine and the sweeper gives them two lease windows from started_at.
            migrationBuilder.Sql("UPDATE workflow_executions SET queued_at = now() WHERE status = 'Queued';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_executions_lease_expires_at",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "IX_workflow_executions_queued_at",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "heartbeat_at",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "lease_owner",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "queued_at",
                table: "workflow_executions");
        }
    }
}
