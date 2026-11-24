using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignSystemLibraryAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "design_system_library_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    variant = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_library_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_design_system_library_assets_design_system_libraries_librar~",
                        column: x => x.library_id,
                        principalTable: "design_system_libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_design_system_library_assets_library_id",
                table: "design_system_library_assets",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_system_library_assets_library_id_name",
                table: "design_system_library_assets",
                columns: new[] { "library_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "design_system_library_assets");
        }
    }
}
