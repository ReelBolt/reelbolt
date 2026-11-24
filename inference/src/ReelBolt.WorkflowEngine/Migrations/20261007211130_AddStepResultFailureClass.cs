using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddStepResultFailureClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "credits_charged_millis",
                table: "workflow_step_results",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "failure_class",
                table: "workflow_step_results",
                type: "text",
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                table: "workflow_step_results",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "credits_charged_millis",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "failure_class",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "workflow_step_results");
        }
    }
}
