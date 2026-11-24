using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Editor v2, P4: the <c>TimelineRender</c> step's config column on <c>workflow_steps</c>. (The
    /// render workflow is hidden through <c>WorkflowDefinitionKind.TimelineRender</c>, a value of the
    /// <c>kind</c> column <c>AddWorkflowDefinitionKind</c> added — no column of its own.)
    /// Hand-written (the <c>dotnet-ef</c> tool cannot start on this host); the model snapshot was
    /// updated by hand to match, so a later <c>migrations add</c> diffs from the right baseline.
    /// </summary>
    public partial class AddTimelineRenderStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "timeline_render_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "timeline_render_config_json",
                table: "workflow_steps");
        }
    }
}
