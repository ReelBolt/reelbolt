using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AcquisitionPrivacyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New-user funnel (docs/design/new-user-funnel.md section 7): the Privacy Policy now describes
            // the tracked-link click counts, the campaign labels kept on visit records and carried in the
            // sign-up link, the welcome questions and the first-step records. Privacy ONLY: the Terms row
            // is not named here and stays at 2026-11-10, so nobody is asked to re-accept the Terms (a
            // Privacy-only bump is an acknowledgement). The row moves only while it still holds the
            // previously shipped default; an operator-published version is never touched. No schema
            // change, so the model snapshot is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-11', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AcquisitionPrivacyVersion' " +
                "WHERE document = 'Privacy' AND version = '2026-11-10' " +
                "AND updated_by IN ('config', 'migration:MarketplaceLegalVersions');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bump this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-10', updated_at = now(), " +
                "updated_by = 'migration:AcquisitionPrivacyVersion:down' " +
                "WHERE document = 'Privacy' AND version = '2026-11-11' " +
                "AND updated_by = 'migration:AcquisitionPrivacyVersion';");
        }
    }
}
