using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOutputModerationVerdicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "output_moderation_verdicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    model_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    output_produced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_output_moderation_verdicts", x => x.id);
                    table.ForeignKey(
                        name: "FK_output_moderation_verdicts_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_output_moderation_verdicts_next_attempt_at",
                table: "output_moderation_verdicts",
                column: "next_attempt_at",
                filter: "next_attempt_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_output_moderation_verdicts_project_id_storage_key",
                table: "output_moderation_verdicts",
                columns: new[] { "project_id", "storage_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "output_moderation_verdicts");
        }
    }
}
