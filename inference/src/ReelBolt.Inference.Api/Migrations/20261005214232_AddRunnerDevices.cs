using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runner_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: true),
                    public_key_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    capabilities_json = table.Column<string>(type: "jsonb", nullable: true),
                    os = table.Column<string>(type: "text", nullable: true),
                    arch = table.Column<string>(type: "text", nullable: true),
                    runner_version = table.Column<string>(type: "text", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paired_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runner_devices", x => x.id);
                    table.CheckConstraint("ck_runner_devices_org_or_pool", "kind = 'CloudPool' OR organization_id IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_runner_devices_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_runner_devices_application_users_paired_by_user_id",
                        column: x => x.paired_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_runner_devices_application_users_revoked_by_user_id",
                        column: x => x.revoked_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_runner_devices_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runner_devices_created_by_user_id",
                table: "runner_devices",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_devices_organization_id",
                table: "runner_devices",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_devices_paired_by_user_id",
                table: "runner_devices",
                column: "paired_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_devices_public_key_fingerprint",
                table: "runner_devices",
                column: "public_key_fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runner_devices_revoked_by_user_id",
                table: "runner_devices",
                column: "revoked_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runner_devices");
        }
    }
}
