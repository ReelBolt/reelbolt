using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AvatarsLegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Avatars: Terms 9.1 moves from a flat ban on depicting an identifiable person to a
            // consent-based rule with new duties (consent record, no minors, no public figures unless
            // self, keep the AI label), plus avatar sentences in sections 11 and 18 (Terms =
            // re-acceptance). Privacy moves with it (avatar data, lawful basis, retention, the depicted
            // person's channel); both are shown together on /accept-terms. Ships in the same release
            // that turns the avatars plan feature on. Each row moves only while it still holds the
            // previously shipped default; an operator-published version is never touched. No schema
            // change, so the model snapshot is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-05', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AvatarsLegalVersions' " +
                "WHERE document = 'Terms' AND version = '2026-10-26' " +
                "AND updated_by IN ('config', 'migration:LaunchAlphaLegalVersions');");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-05', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AvatarsLegalVersions' " +
                "WHERE document = 'Privacy' AND version = '2026-10-31' " +
                "AND updated_by IN ('config', 'migration:PrivacySafetyScreeningVersion');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bumps this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-26', updated_at = now(), " +
                "updated_by = 'migration:AvatarsLegalVersions:down' " +
                "WHERE document = 'Terms' AND version = '2026-11-05' " +
                "AND updated_by = 'migration:AvatarsLegalVersions';");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-31', updated_at = now(), " +
                "updated_by = 'migration:AvatarsLegalVersions:down' " +
                "WHERE document = 'Privacy' AND version = '2026-11-05' " +
                "AND updated_by = 'migration:AvatarsLegalVersions';");
        }
    }
}
