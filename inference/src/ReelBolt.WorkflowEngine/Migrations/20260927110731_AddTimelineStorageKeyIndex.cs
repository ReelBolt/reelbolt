using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Adds TimelineStorageKey column and index to workflow_step_results.
    ///
    /// Backfill strategy: rows relying on the removed per-step-lookup fallback (i.e. those where
    /// timelineStorageKey was stored in output_json and never extracted to the top-level column)
    /// are deliberately NOT backfilled. This is an accepted, documented gap. Only future executions
    /// will set the column explicitly during step completion.
    /// </summary>
    public partial class AddTimelineStorageKeyIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "timeline_storage_key",
                table: "workflow_step_results",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_results_timeline_storage_key",
                table: "workflow_step_results",
                column: "timeline_storage_key");

            // Backfill timeline_storage_key from output_json for existing rows
            migrationBuilder.Sql(
                @"UPDATE workflow_step_results
                  SET timeline_storage_key = output_json->>'timelineStorageKey'
                  WHERE output_json IS NOT NULL
                  AND output_json->>'timelineStorageKey' IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_step_results_timeline_storage_key",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "timeline_storage_key",
                table: "workflow_step_results");
        }
    }
}
