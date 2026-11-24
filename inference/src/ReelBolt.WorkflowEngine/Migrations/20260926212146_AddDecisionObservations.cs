using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decision_observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    agent_type = table.Column<string>(type: "text", nullable: false),
                    site = table.Column<string>(type: "text", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    options_hash = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    probabilities_json = table.Column<string>(type: "jsonb", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    escalated = table.Column<bool>(type: "boolean", nullable: false),
                    downstream_outcome_json = table.Column<string>(type: "jsonb", nullable: true),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_observations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decision_observations_execution_id",
                table: "decision_observations",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_decision_observations_site_created_at",
                table: "decision_observations",
                columns: new[] { "site", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decision_observations");
        }
    }
}
