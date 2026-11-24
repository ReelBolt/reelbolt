using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAppManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_manifest_json",
                table: "workflow_definitions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_in_thread_id",
                table: "workflow_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_via",
                table: "workflow_definitions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_key",
                table: "workflow_definitions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "app_manifest_json",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "created_in_thread_id",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "created_via",
                table: "workflow_definitions");

            migrationBuilder.DropColumn(
                name: "template_key",
                table: "workflow_definitions");
        }
    }
}
