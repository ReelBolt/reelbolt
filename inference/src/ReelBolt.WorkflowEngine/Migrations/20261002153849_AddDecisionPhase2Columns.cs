using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionPhase2Columns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conditional_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_loop_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "agent_tokens_used",
                table: "decision_observations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decision_input_tokens",
                table: "decision_observations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decision_output_tokens",
                table: "decision_observations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conditional_config_json",
                table: "workflow_steps");

            migrationBuilder.DropColumn(
                name: "review_loop_config_json",
                table: "workflow_steps");

            migrationBuilder.DropColumn(
                name: "agent_tokens_used",
                table: "decision_observations");

            migrationBuilder.DropColumn(
                name: "decision_input_tokens",
                table: "decision_observations");

            migrationBuilder.DropColumn(
                name: "decision_output_tokens",
                table: "decision_observations");
        }
    }
}
