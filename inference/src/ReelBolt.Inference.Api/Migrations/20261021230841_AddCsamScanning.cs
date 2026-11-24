using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCsamScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "csam_classification",
                table: "project_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "csam_match_type",
                table: "project_files",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "csam_quarantine_key",
                table: "project_files",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "csam_quarantined_at",
                table: "project_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "csam_scan_claimed_at",
                table: "project_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "csam_scanned_at",
                table: "project_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "csam_status",
                table: "project_files",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotScanned");

            migrationBuilder.CreateTable(
                name: "csam_scan_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    classification = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    match_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    near_match_count = table.Column<int>(type: "integer", nullable: true),
                    pdq_hash_base64 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pdq_quality = table.Column<int>(type: "integer", nullable: true),
                    content_sha256_hex = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    provider_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    quarantine_succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_csam_scan_audits", x => x.id);
                    table.ForeignKey(
                        name: "fk_csam_scan_audits__project_files_project_file_id",
                        column: x => x.project_file_id,
                        principalTable: "project_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_files_csam_status_csam_scan_claimed_at",
                table: "project_files",
                columns: new[] { "csam_status", "csam_scan_claimed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_files_project_id_csam_status",
                table: "project_files",
                columns: new[] { "project_id", "csam_status" });

            migrationBuilder.CreateIndex(
                name: "IX_csam_scan_audits_project_file_id_created_at",
                table: "csam_scan_audits",
                columns: new[] { "project_file_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_csam_scan_audits_project_id",
                table: "csam_scan_audits",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_csam_scan_audits_status_created_at",
                table: "csam_scan_audits",
                columns: new[] { "status", "created_at" });

            // Terms bump: the CSAM hash-matching control added a content-safety section to the
            // Terms. Only the previously shipped default moves; an operator-published version is
            // never touched, and a fresh install gets the new default from LegalDocumentSeeder.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-22', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AddCsamScanning' " +
                "WHERE document = 'Terms' AND version = '2026-10' AND updated_by = 'config';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bump this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10', updated_at = now(), " +
                "updated_by = 'migration:AddCsamScanning:down' " +
                "WHERE document = 'Terms' AND version = '2026-10-22' " +
                "AND updated_by = 'migration:AddCsamScanning';");

            migrationBuilder.DropTable(
                name: "csam_scan_audits");

            migrationBuilder.DropIndex(
                name: "IX_project_files_csam_status_csam_scan_claimed_at",
                table: "project_files");

            migrationBuilder.DropIndex(
                name: "IX_project_files_project_id_csam_status",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_classification",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_match_type",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_quarantine_key",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_quarantined_at",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_scan_claimed_at",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_scanned_at",
                table: "project_files");

            migrationBuilder.DropColumn(
                name: "csam_status",
                table: "project_files");
        }
    }
}
