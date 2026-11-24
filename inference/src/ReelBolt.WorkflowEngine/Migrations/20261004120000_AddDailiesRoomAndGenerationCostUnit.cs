using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddDailiesRoomAndGenerationCostUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // workflow_steps.dailies_room_config_json is added by EnsureDailiesRoomConfigJson (this
            // context owns workflow_steps; the API's AddDailiesRoomConfigJson only covers deployments
            // that already had the table). This one adds only external_generation_jobs.cost_unit.
            // Every pre-existing ledger row was a USD row: the default makes that explicit instead
            // of leaving a null the budget gate would have to interpret.
            migrationBuilder.AddColumn<string>(
                name: "cost_unit",
                table: "external_generation_jobs",
                type: "text",
                nullable: false,
                defaultValue: "usd");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_unit",
                table: "external_generation_jobs");
        }
    }
}
