using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Product design system, P2: the <c>DesignSystem</c> step's config column on
    /// <c>workflow_steps</c>. Hand-written (the <c>dotnet-ef</c> tool cannot start on this host); the
    /// model snapshot was updated by hand to match, so a later <c>migrations add</c> diffs from the
    /// right baseline.
    /// </summary>
    public partial class AddDesignSystemStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "design_system_config_json",
                table: "workflow_steps",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "design_system_config_json",
                table: "workflow_steps");
        }
    }
}
