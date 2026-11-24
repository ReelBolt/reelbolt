using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_analysis_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hold_millis = table.Column<long>(type: "bigint", nullable: false),
                    hold_settled = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_analysis_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_analysis_requests_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_analysis_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    measured_seconds = table.Column<double>(type: "double precision", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_analysis_results", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_analysis_requests_media",
                table: "media_analysis_requests",
                columns: new[] { "project_id", "media_ref", "kind", "language" });

            migrationBuilder.CreateIndex(
                name: "IX_media_analysis_requests_request_id",
                table: "media_analysis_requests",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ux_media_analysis_results_key",
                table: "media_analysis_results",
                columns: new[] { "organization_id", "digest", "kind", "language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_analysis_requests");

            migrationBuilder.DropTable(
                name: "media_analysis_results");
        }
    }
}
