using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceLegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Recipe marketplace: the Terms gain the Community recipes text (publishing, licence, copies,
            // removal). A Terms bump = re-acceptance (all users re-accept); Privacy moves
            // with it and is shown together on /accept-terms. Each row moves only while it still holds
            // the previously shipped default; an operator-published version is never touched. No schema
            // change, so the model snapshot is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-10', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:MarketplaceLegalVersions' " +
                "WHERE document = 'Terms' AND version = '2026-11-05' " +
                "AND updated_by IN ('config', 'migration:AvatarsLegalVersions');");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-10', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:MarketplaceLegalVersions' " +
                "WHERE document = 'Privacy' AND version = '2026-11-05' " +
                "AND updated_by IN ('config', 'migration:AvatarsLegalVersions');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bumps this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-05', updated_at = now(), " +
                "updated_by = 'migration:MarketplaceLegalVersions:down' " +
                "WHERE document = 'Terms' AND version = '2026-11-10' " +
                "AND updated_by = 'migration:MarketplaceLegalVersions';");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-05', updated_at = now(), " +
                "updated_by = 'migration:MarketplaceLegalVersions:down' " +
                "WHERE document = 'Privacy' AND version = '2026-11-10' " +
                "AND updated_by = 'migration:MarketplaceLegalVersions';");
        }
    }
}
