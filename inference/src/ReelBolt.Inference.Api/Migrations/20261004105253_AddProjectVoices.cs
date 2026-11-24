using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectVoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_voices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inference_provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    remote_voice_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference_project_file_ids_json = table.Column<string>(type: "jsonb", nullable: true),
                    consent_attested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consent_attested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consent_statement = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    consent_subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    consent_revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consent_revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retention_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_voices", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_voices_inference_providers_inference_provider_id",
                        column: x => x.inference_provider_id,
                        principalTable: "inference_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_project_voices_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_voices_inference_provider_id",
                table: "project_voices",
                column: "inference_provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_voices_project_id",
                table: "project_voices",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_voices");
        }
    }
}
