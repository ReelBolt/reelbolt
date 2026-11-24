using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCsamOutputHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "csam_output_hold_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            // A project with a flagged file or a matching audit row starts with the marker; moderation-only
            // projects are not touched.
            migrationBuilder.Sql(
                """
                UPDATE projects SET csam_output_hold_at = now()
                WHERE csam_output_hold_at IS NULL
                  AND (EXISTS (SELECT 1 FROM project_files f
                               WHERE f.project_id = projects.id AND f.csam_status = 'Quarantined')
                    OR EXISTS (SELECT 1 FROM csam_scan_audits a
                               WHERE a.project_id = projects.id AND a.status = 'match'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "csam_output_hold_at",
                table: "projects");
        }
    }
}
