using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AvatarLibraryLegalVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Avatars of objects, pictures with avatars, and the workspace avatar library: the Terms (9.1, 11)
            // and the Privacy Policy (providers, avatar paragraph) now describe them. A Terms bump = re-acceptance
            // (all users re-accept); Privacy moves with it and is shown together on /accept-terms. Published
            // on 2026-11-17 by the owner's decision. Each row moves only while it still holds the
            // previously shipped default; an operator-published version is never touched. No schema
            // change, so the model snapshot is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-17', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AvatarLibraryLegalVersions' " +
                "WHERE document = 'Terms' AND version = '2026-11-10' " +
                "AND updated_by IN ('config', 'migration:MarketplaceLegalVersions');");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-17', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AvatarLibraryLegalVersions' " +
                "WHERE document = 'Privacy' AND version = '2026-11-11' " +
                "AND updated_by IN ('config', 'migration:AcquisitionPrivacyVersion');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bumps this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-10', updated_at = now(), " +
                "updated_by = 'migration:AvatarLibraryLegalVersions:down' " +
                "WHERE document = 'Terms' AND version = '2026-11-17' " +
                "AND updated_by = 'migration:AvatarLibraryLegalVersions';");

            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-11-11', updated_at = now(), " +
                "updated_by = 'migration:AvatarLibraryLegalVersions:down' " +
                "WHERE document = 'Privacy' AND version = '2026-11-17' " +
                "AND updated_by = 'migration:AvatarLibraryLegalVersions';");
        }
    }
}
