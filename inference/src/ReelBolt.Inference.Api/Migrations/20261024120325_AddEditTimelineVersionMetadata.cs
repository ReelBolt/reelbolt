using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEditTimelineVersionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cause",
                table: "edit_timeline_snapshots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "milestone");

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "edit_timeline_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "edit_timeline_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "edit_timeline_snapshots",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_edit_timeline_snapshots_expires_at",
                table: "edit_timeline_snapshots",
                column: "expires_at");

            // Existing rows are all milestone / duplicate / import checkpoints. They age out 30 days
            // after creation (D14), except revision 1: the origin never expires, so every listed
            // restore point stays restorable and a timeline never loses its start.
            migrationBuilder.Sql(
                "UPDATE edit_timeline_snapshots SET expires_at = created_at + interval '30 days' WHERE revision <> 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_edit_timeline_snapshots_expires_at",
                table: "edit_timeline_snapshots");

            migrationBuilder.DropColumn(
                name: "cause",
                table: "edit_timeline_snapshots");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "edit_timeline_snapshots");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "edit_timeline_snapshots");

            migrationBuilder.DropColumn(
                name: "name",
                table: "edit_timeline_snapshots");
        }
    }
}
