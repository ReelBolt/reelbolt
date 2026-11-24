using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// contract-migration(UniqueIndex): expand only. The one unique index added to an existing table is ux_project_avatars_variant_live_label on (variant_of_avatar_id, variant_label), filtered to variant_of_avatar_id IS NOT NULL AND consent_revoked_at IS NULL, and both indexed columns are nullable columns this same migration adds: every existing row is NULL there (outside the filter), no released writer sets them, so the old release cannot violate it.

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "variant_description",
                table: "project_avatars",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "variant_label",
                table: "project_avatars",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "variant_of_avatar_id",
                table: "project_avatars",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "avatar_build_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_avatar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_media_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    instruction = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    object_description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    variant_label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    params_json = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    done = table.Column<int>(type: "integer", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    avatar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    statement_locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    allowed_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_avatar_build_jobs", x => x.id);
                    table.CheckConstraint("ck_avatar_build_jobs_allowed_by", "allowed_by IS NULL OR allowed_by IN ('project_consent', 'call')");
                    table.CheckConstraint("ck_avatar_build_jobs_kind", "kind IN ('variant', 'extraction')");
                    table.CheckConstraint("ck_avatar_build_jobs_state", "state IN ('queued', 'pictures', 'videos', 'finding', 'cutting', 'creating', 'ready', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_avatar_build_jobs_project_avatars_avatar_id",
                        column: x => x.avatar_id,
                        principalTable: "project_avatars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_avatar_build_jobs_project_avatars_source_avatar_id",
                        column: x => x.source_avatar_id,
                        principalTable: "project_avatars",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_avatar_build_jobs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_project_avatars_variant_live_label",
                table: "project_avatars",
                columns: new[] { "variant_of_avatar_id", "variant_label" },
                unique: true,
                filter: "variant_of_avatar_id IS NOT NULL AND consent_revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_build_jobs_avatar_id",
                table: "avatar_build_jobs",
                column: "avatar_id");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_build_jobs_project_id_created_at",
                table: "avatar_build_jobs",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_avatar_build_jobs_source_avatar_id",
                table: "avatar_build_jobs",
                column: "source_avatar_id");

            migrationBuilder.CreateIndex(
                name: "IX_avatar_build_jobs_state_updated_at",
                table: "avatar_build_jobs",
                columns: new[] { "state", "updated_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_project_avatars_project_avatars_variant_of_avatar_id",
                table: "project_avatars",
                column: "variant_of_avatar_id",
                principalTable: "project_avatars",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_project_avatars_project_avatars_variant_of_avatar_id",
                table: "project_avatars");

            migrationBuilder.DropTable(
                name: "avatar_build_jobs");

            migrationBuilder.DropIndex(
                name: "ux_project_avatars_variant_live_label",
                table: "project_avatars");

            migrationBuilder.DropColumn(
                name: "variant_description",
                table: "project_avatars");

            migrationBuilder.DropColumn(
                name: "variant_label",
                table: "project_avatars");

            migrationBuilder.DropColumn(
                name: "variant_of_avatar_id",
                table: "project_avatars");
        }
    }
}
