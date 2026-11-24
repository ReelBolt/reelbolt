using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// <c>workflow_step_results.reused_from_step_result_id</c>: set on a row a composition revision's
    /// replay COPIED from the run being changed instead of running the step. Nullable, no FK (the source
    /// row may be in a run that is later deleted). The snapshot also picks up the Inference API's
    /// <c>composition_revisions.cut_policy</c>/<c>replay_plan_json</c> (excluded from this context's
    /// migrations — created by <c>AddCompositionRevisionReplay</c>).
    /// </summary>
    public partial class AddStepResultReusedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reused_from_step_result_id",
                table: "workflow_step_results",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reused_from_step_result_id",
                table: "workflow_step_results");
        }
    }
}
