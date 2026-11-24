using System;
using Microsoft.EntityFrameworkCore.Migrations;

// contract-migration(DropCheckConstraint, AddCheckConstraint): expand only. ck_project_avatar_references_role is dropped and re-added in this one transaction with a SUPERSET of its values (the five person roles plus Front, Angle, Detail), so every row the old release holds or writes still passes. The other CHECK added is on project_avatars.subject_kind, a column this same migration adds NOT NULL DEFAULT 'Person': every existing row and every insert of the old release (which does not name the column) is 'Person'.

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Avatars of objects (docs/design/avatar-objects.md "Avatar subject kind"), expand only:
    /// <c>project_avatars.subject_kind</c> (<c>Person</c> | <c>Object</c>, default <c>Person</c>, so every existing
    /// avatar stays a person), the object roles <c>Front</c> / <c>Angle</c> / <c>Detail</c> added to the reference
    /// role CHECK, and the project-level consent to send avatar reference media to AI providers from the editors and
    /// the assistant (<c>projects.subject_media_consent_at</c> / <c>subject_media_consent_by_user_id</c>, both
    /// nullable). All three tables are API-owned; nothing the API maps <c>ExcludeFromMigrations()</c> is touched.
    /// </summary>
    public partial class AddAvatarObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_project_avatar_references_role",
                table: "project_avatar_references");

            migrationBuilder.AddColumn<DateTime>(
                name: "subject_media_consent_at",
                table: "projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subject_media_consent_by_user_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "subject_kind",
                table: "project_avatars",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Person");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_avatars_subject_kind",
                table: "project_avatars",
                sql: "subject_kind IN ('Person', 'Object')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_avatar_references_role",
                table: "project_avatar_references",
                sql: "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_project_avatars_subject_kind",
                table: "project_avatars");

            migrationBuilder.DropCheckConstraint(
                name: "ck_project_avatar_references_role",
                table: "project_avatar_references");

            migrationBuilder.DropColumn(
                name: "subject_media_consent_at",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "subject_media_consent_by_user_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "subject_kind",
                table: "project_avatars");

            migrationBuilder.AddCheckConstraint(
                name: "ck_project_avatar_references_role",
                table: "project_avatar_references",
                sql: "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech')");
        }
    }
}
