using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Converges the <c>powerbi_ro</c> grants on the Inference-API-owned tables. The role's lifecycle
    /// and its read-only hardening stay the operator script's
    /// (<c>infra/powerbi/postgres-readonly-role.sql</c>): creating or altering a role needs CREATEROLE,
    /// and every migration runs as a principal that is LOGIN CREATEDB NOSUPERUSER by contract, so a
    /// migration that touched a role would abort the bootstrap with 42501. Granting and revoking need no
    /// CREATEROLE, so they live here and every deploy converges a database that carries an older, wider
    /// ACL instead of relying on the operator to re-run the script. Both statements are guarded on
    /// <c>powerbi_ro</c> existing, so a fresh database (role absent) is a no-op.
    /// </summary>
    /// <inheritdoc />
    public partial class AddPowerBiReadOnlyRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Frozen at this migration's creation: the live constant now names columns such as
            // application_users.is_service_account that are created by a later migration, and a
            // fresh database with the cluster-wide powerbi_ro role present runs this migration
            // first, so referencing the live text would abort with 42703.
            migrationBuilder.Sql(PowerBiGrantsFrozen20261020.GrantsSql);
            migrationBuilder.Sql(PowerBiReadOnlyRoleSql.AssertionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No inverse. This migration converges the ACL to the grant set PowerBiReadOnlyRoleSql
            // describes; a convergence has nothing to roll back to, so this Down is deliberately a
            // no-op. It must NOT call PowerBiReadOnlyRoleSql.Down: that DROPs the powerbi_ro role, so
            // rolling back would remove the role and break the nightly extract. Role lifecycle belongs
            // to the operator script alone.
        }
    }
}
