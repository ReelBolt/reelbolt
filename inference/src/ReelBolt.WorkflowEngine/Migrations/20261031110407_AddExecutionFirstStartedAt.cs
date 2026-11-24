using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionFirstStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "first_started_at",
                table: "workflow_executions",
                type: "timestamp with time zone",
                nullable: true);

            // A run that has started keeps the start it has; a run that never started stays null (a gate treats null as
            // "may predate").
            migrationBuilder.Sql(
                "UPDATE workflow_executions SET first_started_at = started_at WHERE first_started_at IS NULL AND started_at IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "first_started_at",
                table: "workflow_executions");
        }
    }
}
