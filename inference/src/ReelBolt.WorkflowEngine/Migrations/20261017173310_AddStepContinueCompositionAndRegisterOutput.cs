using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddStepContinueCompositionAndRegisterOutput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "continue_composition_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "register_output_as_project_file",
                table: "workflow_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "continue_composition_json",
                table: "workflow_steps");

            migrationBuilder.DropColumn(
                name: "register_output_as_project_file",
                table: "workflow_steps");
        }
    }
}
