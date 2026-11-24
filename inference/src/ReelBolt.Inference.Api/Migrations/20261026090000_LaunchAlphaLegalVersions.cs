using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class LaunchAlphaLegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Launch-alpha legal rewrite: the contract party is now a named person, with a new
            // 14-day refund promise and a forum clause (Terms = re-acceptance). Privacy moves with
            // it. Each row moves only while it still holds the previously shipped default (seeded
            // by config, or bumped by the earlier migration); an operator-published version is
            // never touched. No schema change, so the model snapshot is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-26', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:LaunchAlphaLegalVersions' " +
                "WHERE document = 'Terms' AND version IN ('2026-10', '2026-10-22') " +
                "AND updated_by IN ('config', 'migration:AddCsamScanning');");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-26', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:LaunchAlphaLegalVersions' " +
                "WHERE document = 'Privacy' AND version IN ('2026-10', '2026-10-12') " +
                "AND updated_by IN ('config', 'migration:AddPromotionsAndMarketingConsent');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bumps this migration made itself. The Up guard also matches a row a
            // fresh install seeded with the older '2026-10' default, and the row does not record
            // which previous value it held, so Down reverts to the later shipped default
            // ('2026-10-22' Terms, '2026-10-12' Privacy).
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-22', updated_at = now(), " +
                "updated_by = 'migration:LaunchAlphaLegalVersions:down' " +
                "WHERE document = 'Terms' AND version = '2026-10-26' " +
                "AND updated_by = 'migration:LaunchAlphaLegalVersions';");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-12', updated_at = now(), " +
                "updated_by = 'migration:LaunchAlphaLegalVersions:down' " +
                "WHERE document = 'Privacy' AND version = '2026-10-26' " +
                "AND updated_by = 'migration:LaunchAlphaLegalVersions';");
        }
    }
}
