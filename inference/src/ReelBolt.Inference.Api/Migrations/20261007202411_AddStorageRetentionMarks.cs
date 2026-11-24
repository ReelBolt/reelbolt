using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageRetentionMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "storage_retention_marks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    marked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    purged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_retention_marks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_storage_retention_marks_organization_id_marked_at",
                table: "storage_retention_marks",
                columns: new[] { "organization_id", "marked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_retention_marks_storage_key",
                table: "storage_retention_marks",
                column: "storage_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_retention_marks");
        }
    }
}
