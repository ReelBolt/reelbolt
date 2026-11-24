using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPronunciationEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_pronunciation_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    spelling = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    case_sensitive = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_pronunciation_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_pronunciation_entries_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_project_pronunciation_entries_project_term",
                table: "project_pronunciation_entries",
                columns: new[] { "project_id", "term" },
                unique: true);

            // Case-insensitive uniqueness: "Novara" and "novara" are the same glossary term.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_project_pronunciation_entries_project_lower_term " +
                "ON project_pronunciation_entries (project_id, lower(term));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_pronunciation_entries");
        }
    }
}
