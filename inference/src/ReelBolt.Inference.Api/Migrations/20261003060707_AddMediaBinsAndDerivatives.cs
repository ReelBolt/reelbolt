using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaBinsAndDerivatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The six added columns are the media-derivative cache identity plus the source
            // discriminator. `media_derivatives` exists already (AddEditTimelineTables) and is only
            // extended here — never re-declared. It has no writer, so it is empty; the NOT NULL
            // columns still carry a defaultValue because that is what makes the DDL safe on a table
            // that does hold rows, and because a required column cannot be added without one.
            migrationBuilder.AlterColumn<Guid>(
                name: "source_project_file_id",
                table: "media_derivatives",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "params_hash",
                table: "media_derivatives",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            // "{}" and not "": this column is jsonb, and Postgres rejects DEFAULT '' on jsonb
            // (invalid input syntax for type json). Matches MediaDerivative.ParamsJson's initializer.
            migrationBuilder.AddColumn<string>(
                name: "params_json",
                table: "media_derivatives",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "source_etag",
                table: "media_derivatives",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // "ProjectFile" and not "": the value is persisted as a string, so a default that is not
            // a member of the enum would make such a row unreadable. Any row predating this migration
            // could only have been sourced from a project file — source_project_file_id was NOT NULL
            // until the AlterColumn above — so ProjectFile is the only correct default.
            migrationBuilder.AddColumn<string>(
                name: "source_kind",
                table: "media_derivatives",
                type: "text",
                nullable: false,
                defaultValue: "ProjectFile");

            migrationBuilder.AddColumn<Guid>(
                name: "source_step_result_id",
                table: "media_derivatives",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_storage_key",
                table: "media_derivatives",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "media_bins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    parent_bin_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_bins", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_bins_media_bins_parent_bin_id",
                        column: x => x.parent_bin_id,
                        principalTable: "media_bins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_bins_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_bin_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "text", nullable: false),
                    source_project_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_bin_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_bin_items_media_bins_bin_id",
                        column: x => x.bin_id,
                        principalTable: "media_bins",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_media_derivatives_identity",
                table: "media_derivatives",
                columns: new[] { "source_storage_key", "source_etag", "params_hash" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_media_bin_items_bin_id",
                table: "media_bin_items",
                column: "bin_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_bins_parent_bin_id",
                table: "media_bins",
                column: "parent_bin_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_bins_project_id",
                table: "media_bins",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_bin_items");

            migrationBuilder.DropTable(
                name: "media_bins");

            migrationBuilder.DropIndex(
                name: "ux_media_derivatives_identity",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "params_hash",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "params_json",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "source_etag",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "source_kind",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "source_step_result_id",
                table: "media_derivatives");

            migrationBuilder.DropColumn(
                name: "source_storage_key",
                table: "media_derivatives");

            migrationBuilder.AlterColumn<Guid>(
                name: "source_project_file_id",
                table: "media_derivatives",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
