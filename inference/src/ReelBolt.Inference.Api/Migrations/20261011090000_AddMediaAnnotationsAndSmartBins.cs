using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Editor v2, P6: the user's annotations on media sources (<c>media_annotations</c>) and the smart
    /// bin filter column on <c>media_bins</c>. Hand-written (the <c>dotnet-ef</c> tool cannot start on
    /// this host); the model snapshot was updated by hand to match, so a later <c>migrations add</c>
    /// diffs from the right baseline.
    /// </summary>
    public partial class AddMediaAnnotationsAndSmartBins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "smart_filter_json",
                table: "media_bins",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "media_annotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    source_project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    keywords_json = table.Column<string>(type: "jsonb", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    color_label = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    favourite = table.Column<bool>(type: "boolean", nullable: false),
                    flag = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_annotations", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_annotations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_media_annotations_project_id",
                table: "media_annotations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ux_media_annotations_source",
                table: "media_annotations",
                columns: new[] { "project_id", "source_kind", "source_project_file_id", "source_step_result_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "media_annotations");

            migrationBuilder.DropColumn(
                name: "smart_filter_json",
                table: "media_bins");
        }
    }
}
