using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewScoreStepResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "step_result_id",
                table: "review_scores",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_scores_step_result_id",
                table: "review_scores",
                column: "step_result_id");

            migrationBuilder.AddForeignKey(
                name: "FK_review_scores_workflow_step_results_step_result_id",
                table: "review_scores",
                column: "step_result_id",
                principalTable: "workflow_step_results",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_scores_workflow_step_results_step_result_id",
                table: "review_scores");

            migrationBuilder.DropIndex(
                name: "IX_review_scores_step_result_id",
                table: "review_scores");

            migrationBuilder.DropColumn(
                name: "step_result_id",
                table: "review_scores");
        }
    }
}
