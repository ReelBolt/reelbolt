using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddStepResultTokenSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "input_tokens",
                table: "workflow_step_results",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "output_tokens",
                table: "workflow_step_results",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_model_name",
                table: "workflow_step_results",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "primary_provider_id",
                table: "workflow_step_results",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_tokens",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "output_tokens",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "primary_model_name",
                table: "workflow_step_results");

            migrationBuilder.DropColumn(
                name: "primary_provider_id",
                table: "workflow_step_results");
        }
    }
}
