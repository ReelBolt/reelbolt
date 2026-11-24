using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.WorkflowEngine.Migrations
{
    /// <summary>
    /// Converges the <c>powerbi_ro</c> grants on the three WorkflowEngine-owned tables. The role's
    /// lifecycle and its read-only hardening stay the operator script's
    /// (<c>infra/powerbi/postgres-readonly-role.sql</c>): creating or altering a role needs CREATEROLE,
    /// and every migration runs as a principal that is LOGIN CREATEDB NOSUPERUSER by contract, so a
    /// migration that touched a role would abort the bootstrap with 42501. Granting and revoking need no
    /// CREATEROLE, so they live here and every deploy converges a database that carries an older, wider
    /// ACL instead of relying on the operator to re-run the script. The block is guarded on
    /// <c>powerbi_ro</c> existing, so a fresh database (role absent) is a no-op.
    /// </summary>
    /// <inheritdoc />
    public partial class AddPowerBiReadOnlyRoleEngineGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Frozen at this migration's creation: the live constant now names
            // workflow_executions.attention_json, first_started_at and api_key_id, which are created
            // by later migrations, and a fresh database with the cluster-wide powerbi_ro role
            // present runs this migration first, so referencing the live text would abort with 42703.
            migrationBuilder.Sql(PowerBiGrantsFrozen20261020.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No inverse. This migration converges the ACL to the grant set PowerBiReadOnlyRoleEngineSql
            // describes; a convergence has nothing to roll back to, so this Down is deliberately a
            // no-op. It must NOT call PowerBiReadOnlyRoleEngineSql.Down: rolling a convergence back to a
            // wider ACL is never wanted. Role lifecycle belongs to the operator script alone.
        }
    }
}
