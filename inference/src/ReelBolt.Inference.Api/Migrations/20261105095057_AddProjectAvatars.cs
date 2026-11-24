using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_avatar_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    avatar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consent_statement_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    outcomes_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_avatar_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_avatars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    project_voice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_attested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_attested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consent_statement = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    consent_statement_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    consent_locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    consent_subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    consent_basis = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    consent_subject_adult = table.Column<bool>(type: "boolean", nullable: false),
                    consent_document_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consent_revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retention_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    disclosure_label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_avatars", x => x.id);
                    table.CheckConstraint("ck_project_avatars_consent_basis", "consent_basis IN ('Self', 'WrittenPermission')");
                    table.CheckConstraint("ck_project_avatars_disclosure_label", "disclosure_label IN ('Visible', 'MetadataOnly')");
                    table.CheckConstraint("ck_project_avatars_revoked_pair", "(consent_revoked_at IS NULL) = (consent_revoked_by_user_id IS NULL)");
                    table.ForeignKey(
                        name: "FK_project_avatars_project_files_consent_document_file_id",
                        column: x => x.consent_document_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_avatars_project_voices_project_voice_id",
                        column: x => x.project_voice_id,
                        principalTable: "project_voices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_avatars_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_avatar_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    avatar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    attached_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_avatar_references", x => x.id);
                    table.CheckConstraint("ck_project_avatar_references_kind", "kind IN ('Image', 'Video', 'Audio')");
                    table.CheckConstraint("ck_project_avatar_references_role", "role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech')");
                    table.ForeignKey(
                        name: "FK_project_avatar_references_project_avatars_avatar_id",
                        column: x => x.avatar_id,
                        principalTable: "project_avatars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_avatar_references_project_files_project_file_id",
                        column: x => x.project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "project_avatar_vendor_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    avatar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_capability_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remote_asset_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    uploaded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    vendor_expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    delete_outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_avatar_vendor_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_avatar_vendor_assets_model_capabilities_model_capab~",
                        column: x => x.model_capability_id,
                        principalTable: "model_capabilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_avatar_vendor_assets_project_avatar_references_refe~",
                        column: x => x.reference_id,
                        principalTable: "project_avatar_references",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_avatar_vendor_assets_project_avatars_avatar_id",
                        column: x => x.avatar_id,
                        principalTable: "project_avatars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_events_avatar_id_occurred_at",
                table: "project_avatar_events",
                columns: new[] { "avatar_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_events_occurred_at",
                table: "project_avatar_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_events_project_id",
                table: "project_avatar_events",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_references_avatar_id_ordinal",
                table: "project_avatar_references",
                columns: new[] { "avatar_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_references_avatar_id_project_file_id",
                table: "project_avatar_references",
                columns: new[] { "avatar_id", "project_file_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_references_project_file_id",
                table: "project_avatar_references",
                column: "project_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_vendor_assets_avatar_id",
                table: "project_avatar_vendor_assets",
                column: "avatar_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_vendor_assets_model_capability_id",
                table: "project_avatar_vendor_assets",
                column: "model_capability_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatar_vendor_assets_reference_id",
                table: "project_avatar_vendor_assets",
                column: "reference_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatars_consent_document_file_id",
                table: "project_avatars",
                column: "consent_document_file_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatars_project_id",
                table: "project_avatars",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_avatars_project_voice_id",
                table: "project_avatars",
                column: "project_voice_id");

            migrationBuilder.CreateIndex(
                name: "ux_project_avatars_project_live_name",
                table: "project_avatars",
                columns: new[] { "project_id", "name" },
                unique: true,
                filter: "consent_revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_avatar_events");

            migrationBuilder.DropTable(
                name: "project_avatar_vendor_assets");

            migrationBuilder.DropTable(
                name: "project_avatar_references");

            migrationBuilder.DropTable(
                name: "project_avatars");
        }
    }
}
