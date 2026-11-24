using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddTenancyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "workflow_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "external_generation_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "decision_observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "decision_observations",
                type: "uuid",
                nullable: true);

            // Backfill through projects (whose organization_id the Inference API migration, which
            // always runs first, has already filled), then tighten the executions column.
            migrationBuilder.Sql(
                @"UPDATE workflow_executions e SET organization_id = p.organization_id
FROM projects p
WHERE p.id = e.project_id;");

            migrationBuilder.Sql(
                @"UPDATE decision_observations d SET project_id = e.project_id, organization_id = e.organization_id
FROM workflow_executions e
WHERE e.id = d.execution_id;");

            migrationBuilder.Sql(
                @"UPDATE external_generation_jobs j SET organization_id = p.organization_id
FROM projects p
WHERE p.id = j.project_id;");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "workflow_executions",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_executions_organization_id",
                table: "workflow_executions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_generation_jobs_organization_id_created_at",
                table: "external_generation_jobs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_decision_observations_organization_id",
                table: "decision_observations",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_executions_organization_id",
                table: "workflow_executions");

            migrationBuilder.DropIndex(
                name: "IX_external_generation_jobs_organization_id_created_at",
                table: "external_generation_jobs");

            migrationBuilder.DropIndex(
                name: "IX_decision_observations_organization_id",
                table: "decision_observations");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "workflow_executions");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "external_generation_jobs");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "decision_observations");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "decision_observations");
        }
    }
}
