using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Product design system, P2: the per-run pin column <c>design_system_pin_json</c> on
    /// <c>workflow_executions</c> (SPEC 3.5). Hand-written (the <c>dotnet-ef</c> tool cannot start on
    /// this host); the model snapshot was updated by hand to match, so a later <c>migrations add</c>
    /// diffs from the right baseline.
    /// </summary>
    public partial class AddExecutionDesignSystemPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "design_system_pin_json",
                table: "workflow_executions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "design_system_pin_json",
                table: "workflow_executions");
        }
    }
}
