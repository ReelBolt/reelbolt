using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddFailedStepResultWindowIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_workflow_step_results_failed_completed_at",
                table: "workflow_step_results",
                column: "completed_at",
                filter: "status = 'Failed'");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_step_results_failed_uncompleted_executed_at",
                table: "workflow_step_results",
                column: "executed_at",
                filter: "status = 'Failed' AND completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_step_results_failed_completed_at",
                table: "workflow_step_results");

            migrationBuilder.DropIndex(
                name: "ix_workflow_step_results_failed_uncompleted_executed_at",
                table: "workflow_step_results");
        }
    }
}
