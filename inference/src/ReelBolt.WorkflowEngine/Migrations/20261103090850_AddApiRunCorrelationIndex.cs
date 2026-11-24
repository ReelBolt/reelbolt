using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;

// contract-migration(UniqueIndex, HelperSql): expand only; the unique index is filtered to the new apirun: correlation prefix that the old release never writes, and the Sql is a column grant

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <inheritdoc />
    public partial class AddApiRunCorrelationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_workflow_executions_api_run_correlation",
                table: "workflow_executions",
                column: "correlation_id",
                unique: true,
                filter: "correlation_id LIKE 'apirun:%'");

            // The BI role's head grant block names api_key_id, but the earlier engine grant migration
            // is frozen to its creation date and cannot. Grant the column here, where it now exists.
            migrationBuilder.Sql(PowerBiReadOnlyRoleEngineSql.ApiKeyIdColumnGrantSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_workflow_executions_api_run_correlation",
                table: "workflow_executions");
        }
    }
}
