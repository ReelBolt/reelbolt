using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// <c>workflow_steps.dailies_room_config_json</c> on the context that owns <c>workflow_steps</c>.
    /// The column was first added by the Inference API's <c>AddDailiesRoomConfigJson</c>, which on a new
    /// database runs before this engine has created the table at all; that migration is now guarded to
    /// skip a missing table, so this one adds the column there. Idempotent (<c>IF NOT EXISTS</c>) because
    /// every existing deployment already has it. Hand-written; the snapshot already carries the column.
    /// </summary>
    public partial class EnsureDailiesRoomConfigJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE workflow_steps ADD COLUMN IF NOT EXISTS dailies_room_config_json jsonb;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing: on an existing deployment the column predates this migration (the API added
            // it), so dropping it here would take away data this migration never created.
        }
    }
}
