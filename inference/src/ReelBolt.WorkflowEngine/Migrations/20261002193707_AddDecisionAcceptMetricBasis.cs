using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionAcceptMetricBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accept_metric",
                table: "decision_observations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "score_cutoff",
                table: "decision_observations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accept_metric",
                table: "decision_observations");

            migrationBuilder.DropColumn(
                name: "score_cutoff",
                table: "decision_observations");
        }
    }
}
