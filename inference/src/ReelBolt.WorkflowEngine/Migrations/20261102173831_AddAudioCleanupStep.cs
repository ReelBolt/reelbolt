using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddAudioCleanupStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audio_cleanup_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_cleanup_config_json",
                table: "workflow_steps");
        }
    }
}
