using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddPinInsertStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pin_insert_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pin_insert_config_json",
                table: "workflow_steps");
        }
    }
}
