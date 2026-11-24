using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerJobsAndSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runner_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    target = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    required_caps_json = table.Column<string>(type: "jsonb", nullable: true),
                    spec_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner_runner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_epoch = table.Column<long>(type: "bigint", nullable: false),
                    lease_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    gateway_instance = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_class = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runner_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_runner_jobs__workflow_executions_execution_id",
                        column: x => x.execution_id,
                        principalTable: "workflow_executions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runner_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gateway_instance = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_op_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runner_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_runner_sessions_runner_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "runner_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runner_jobs_execution_id",
                table: "runner_jobs",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_jobs_idempotency_key",
                table: "runner_jobs",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runner_jobs_lease_expires_at",
                table: "runner_jobs",
                column: "lease_expires_at",
                filter: "status IN ('Assigned', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_runner_jobs_status_target_organization_id_created_at",
                table: "runner_jobs",
                columns: new[] { "status", "target", "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_runner_sessions_execution_id",
                table: "runner_sessions",
                column: "execution_id",
                unique: true,
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_runner_sessions_job_id",
                table: "runner_sessions",
                column: "job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runner_sessions");

            migrationBuilder.DropTable(
                name: "runner_jobs");
        }
    }
}
