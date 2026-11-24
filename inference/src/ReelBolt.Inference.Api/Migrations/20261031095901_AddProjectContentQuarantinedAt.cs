using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectContentQuarantinedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "content_quarantined_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            // Engine output rows keep the instant their output was produced, so a move or rename (which rewrite
            // uploaded_at, and a move the category and key) cannot make an old output look new. Rows the three earlier
            // identifiers match start with their upload time: the best that is known. A legacy output already moved out of
            // its category cannot be identified.
            migrationBuilder.AddColumn<DateTime>(
                name: "output_produced_at",
                table: "project_files",
                type: "timestamp with time zone",
                nullable: true);
            migrationBuilder.Sql(
                """
                UPDATE project_files SET output_produced_at = uploaded_at
                WHERE output_produced_at IS NULL
                  AND (category = 'outputFiles' OR source_kind = 'output_copy' OR storage_key LIKE '%/agentFiles/generated/%');
                """);

            // A project that already holds a quarantined file starts with a marker, so earlier output is not reused.
            migrationBuilder.Sql(
                """
                UPDATE projects SET content_quarantined_at = now()
                WHERE content_quarantined_at IS NULL
                  AND EXISTS (SELECT 1 FROM project_files f
                              WHERE f.project_id = projects.id
                                AND (f.csam_status = 'Quarantined' OR f.moderation_status = 'Quarantined'));
                """);

            // A quarantined file that has since been deleted or released leaves no row, but its audit stays: a project
            // with a past quarantine audit row starts with a marker at the latest such row's time. The audit tables
            // carry a project id (csam_scan_audits.project_id; content_moderation_decisions.project_id, ingest blocks
            // only: a blocked prompt quarantines no file). Folded into this migration, which has not shipped to a
            // deployed environment.
            migrationBuilder.Sql(
                """
                UPDATE projects SET content_quarantined_at = a.at
                FROM (SELECT project_id, max(created_at) AS at FROM csam_scan_audits
                      WHERE project_id IS NOT NULL AND status = 'match'
                      GROUP BY project_id) a
                WHERE projects.id = a.project_id AND projects.content_quarantined_at IS NULL;
                """);
            migrationBuilder.Sql(
                """
                UPDATE projects SET content_quarantined_at = a.at
                FROM (SELECT project_id, max(created_at) AS at FROM content_moderation_decisions
                      WHERE project_id IS NOT NULL AND surface = 'Ingest' AND action = 'Block'
                      GROUP BY project_id) a
                WHERE projects.id = a.project_id AND projects.content_quarantined_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "output_produced_at",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "content_quarantined_at",
                table: "projects");
        }
    }
}
