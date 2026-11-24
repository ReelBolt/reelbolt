using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "folder_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    parent_folder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_folders", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_folders_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_tags_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_tag_links",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_tag_links", x => new { x.project_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_project_tag_links_project_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "project_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_tag_links_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_projects_folder_id",
                table: "projects",
                column: "folder_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_folders_organization_id_name_key",
                table: "project_folders",
                columns: new[] { "organization_id", "name_key" },
                unique: true,
                filter: "parent_folder_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_project_folders_organization_id_parent_folder_id_name_key",
                table: "project_folders",
                columns: new[] { "organization_id", "parent_folder_id", "name_key" },
                unique: true,
                filter: "parent_folder_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_project_tag_links_tag_id",
                table: "project_tag_links",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_tags_organization_id_name_key",
                table: "project_tags",
                columns: new[] { "organization_id", "name_key" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_projects__project_folders_folder_id",
                table: "projects",
                column: "folder_id",
                principalTable: "project_folders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_projects__project_folders_folder_id",
                table: "projects");

            migrationBuilder.DropTable(
                name: "project_folders");

            migrationBuilder.DropTable(
                name: "project_tag_links");

            migrationBuilder.DropTable(
                name: "project_tags");

            migrationBuilder.DropIndex(
                name: "ix_projects_folder_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "folder_id",
                table: "projects");
        }
    }
}
