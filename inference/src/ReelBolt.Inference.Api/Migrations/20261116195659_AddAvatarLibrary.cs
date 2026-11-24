using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// contract-migration(UniqueIndex): expand only. The one unique index added to an existing table is ux_project_avatars_project_library_item on (project_id, library_item_id), and library_item_id is a nullable column this same migration adds: every existing row is NULL there, no released writer sets the column, and NULLs never collide in a unique index, so the old release cannot violate it.

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "library_item_id",
                table: "project_avatars",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "avatar_library_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    consent_statement = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    consent_statement_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    consent_locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    consent_subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    consent_basis = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    consent_subject_adult = table.Column<bool>(type: "boolean", nullable: false),
                    consent_attested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_attested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consent_revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consent_revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_document_storage_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    retention_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    disclosure_label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_library_items", x => x.id);
                    table.CheckConstraint("ck_avatar_library_items_consent_basis", "consent_basis IN ('Self', 'WrittenPermission')");
                    table.CheckConstraint("ck_avatar_library_items_disclosure_label", "disclosure_label IN ('Visible', 'MetadataOnly')");
                    table.CheckConstraint("ck_avatar_library_items_revoked_pair", "(consent_revoked_at IS NULL) = (consent_revoked_by_user_id IS NULL)");
                    table.CheckConstraint("ck_avatar_library_items_subject_kind", "subject_kind IN ('Person', 'Object')");
                    table.ForeignKey(
                        name: "FK_avatar_library_items_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "avatar_library_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    attached_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_library_references", x => x.id);
                    table.CheckConstraint("ck_avatar_library_references_kind", "kind IN ('Image', 'Video', 'Audio')");
                    table.CheckConstraint("ck_avatar_library_references_role", "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail')");
                    table.ForeignKey(
                        name: "fk_avatar_library_references_library_item",
                        column: x => x.library_item_id,
                        principalTable: "avatar_library_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_avatars_library_item_id",
                table: "project_avatars",
                column: "library_item_id");

            migrationBuilder.CreateIndex(
                name: "ux_project_avatars_project_library_item",
                table: "project_avatars",
                columns: new[] { "project_id", "library_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_avatar_library_items_organization_id_subject_kind",
                table: "avatar_library_items",
                columns: new[] { "organization_id", "subject_kind" });

            migrationBuilder.CreateIndex(
                name: "IX_avatar_library_items_retention_until",
                table: "avatar_library_items",
                column: "retention_until");

            migrationBuilder.CreateIndex(
                name: "ux_avatar_library_items_org_live_name",
                table: "avatar_library_items",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "consent_revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_library_references_library_item_id_ordinal",
                table: "avatar_library_references",
                columns: new[] { "library_item_id", "ordinal" });

            migrationBuilder.AddForeignKey(
                name: "FK_project_avatars_avatar_library_items_library_item_id",
                table: "project_avatars",
                column: "library_item_id",
                principalTable: "avatar_library_items",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_project_avatars_avatar_library_items_library_item_id",
                table: "project_avatars");

            migrationBuilder.DropTable(
                name: "avatar_library_references");

            migrationBuilder.DropTable(
                name: "avatar_library_items");

            migrationBuilder.DropIndex(
                name: "IX_project_avatars_library_item_id",
                table: "project_avatars");

            migrationBuilder.DropIndex(
                name: "ux_project_avatars_project_library_item",
                table: "project_avatars");

            migrationBuilder.DropColumn(
                name: "library_item_id",
                table: "project_avatars");
        }
    }
}
