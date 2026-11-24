using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDailiesRoomConfigJson : Migration
    {
        /// <summary>
        /// Issue #93 phase 4: the DailiesRoom step's config column. <c>workflow_steps</c> belongs to the
        /// WorkflowEngine (this context maps it with <c>ExcludeFromMigrations()</c>), and on a NEW
        /// database the engine only creates it after this API is healthy — so an unconditional
        /// <c>ALTER TABLE workflow_steps</c> here crashed every fresh install at startup. The statement
        /// is guarded to do nothing when the table does not exist yet; the engine's
        /// <c>EnsureDailiesRoomConfigJson</c> migration adds the column in that case. Deployments that
        /// already ran the original version keep the column it added (the history row is unchanged).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE IF EXISTS workflow_steps ADD COLUMN IF NOT EXISTS dailies_room_config_json jsonb;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE IF EXISTS workflow_steps DROP COLUMN IF EXISTS dailies_room_config_json;");
        }
    }
}
