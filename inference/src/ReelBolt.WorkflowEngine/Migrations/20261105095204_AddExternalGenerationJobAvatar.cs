using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalGenerationJobAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "avatar_id",
                table: "external_generation_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_generation_jobs_avatar_id",
                table: "external_generation_jobs",
                column: "avatar_id",
                filter: "avatar_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_external_generation_jobs_avatar_id",
                table: "external_generation_jobs");

            migrationBuilder.DropColumn(
                name: "avatar_id",
                table: "external_generation_jobs");
        }
    }
}
