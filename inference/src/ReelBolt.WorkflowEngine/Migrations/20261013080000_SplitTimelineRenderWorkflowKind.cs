using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// <c>WorkflowDefinitionKind.TimelineRender</c> was declared as 3, the value
    /// <c>CompositionReplay</c> also took, and <c>workflow_definitions.kind</c> is stored as an
    /// integer: the engine ran every editor render as a composition replay (and failed it). The enum
    /// now stores <c>TimelineRender</c> as 4; this moves the rows already written. A render workflow is
    /// the editor's one-step workflow whose steps are all <c>TimelineRender</c>; a replay snapshot
    /// always carries a <c>Revise</c> step, so it never matches. Data only; no schema or snapshot change.
    /// </summary>
    public partial class SplitTimelineRenderWorkflowKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE workflow_definitions w
                SET kind = 4
                WHERE w.kind = 3
                  AND EXISTS (SELECT 1 FROM workflow_steps s
                              WHERE s.workflow_definition_id = w.id AND s.step_type = 'TimelineRender')
                  AND NOT EXISTS (SELECT 1 FROM workflow_steps s
                                  WHERE s.workflow_definition_id = w.id AND s.step_type <> 'TimelineRender');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE workflow_definitions SET kind = 3 WHERE kind = 4;");
        }
    }
}
