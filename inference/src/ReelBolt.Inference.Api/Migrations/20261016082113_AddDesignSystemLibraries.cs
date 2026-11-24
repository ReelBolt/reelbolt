using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDesignSystemLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_library_id",
                table: "design_systems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "design_system_libraries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    design_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_system_libraries", x => x.id);
                    table.ForeignKey(
                        name: "FK_design_system_libraries_design_systems_design_system_id",
                        column: x => x.design_system_id,
                        principalTable: "design_systems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_design_system_libraries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_design_systems_parent_library_id",
                table: "design_systems",
                column: "parent_library_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_design_systems_parent_library_project",
                table: "design_systems",
                sql: "parent_library_id IS NULL OR project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_design_system_libraries_design_system_id",
                table: "design_system_libraries",
                column: "design_system_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_design_system_libraries_organization_id",
                table: "design_system_libraries",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_design_system_libraries_organization_id_name",
                table: "design_system_libraries",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_design_systems_design_system_libraries_parent_library_id",
                table: "design_systems",
                column: "parent_library_id",
                principalTable: "design_system_libraries",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_design_systems_design_system_libraries_parent_library_id",
                table: "design_systems");

            migrationBuilder.DropTable(
                name: "design_system_libraries");

            migrationBuilder.DropIndex(
                name: "IX_design_systems_parent_library_id",
                table: "design_systems");

            migrationBuilder.DropCheckConstraint(
                name: "ck_design_systems_parent_library_project",
                table: "design_systems");

            migrationBuilder.DropColumn(
                name: "parent_library_id",
                table: "design_systems");
        }
    }
}
