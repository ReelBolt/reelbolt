using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class PrivacySafetyScreeningVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Privacy Policy now states that uploads and created videos are automatically
            // safety-screened. Privacy only, so this is an acknowledgement and the Terms are not asked
            // for again. The row moves only while it still holds the previously shipped default; an
            // operator-published version is never touched. No schema change, so the model snapshot
            // is unchanged.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-31', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:PrivacySafetyScreeningVersion' " +
                "WHERE document = 'Privacy' AND version = '2026-10-26' " +
                "AND updated_by IN ('config', 'migration:LaunchAlphaLegalVersions');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bump this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-26', updated_at = now(), " +
                "updated_by = 'migration:PrivacySafetyScreeningVersion:down' " +
                "WHERE document = 'Privacy' AND version = '2026-10-31' " +
                "AND updated_by = 'migration:PrivacySafetyScreeningVersion';");
        }
    }
}
