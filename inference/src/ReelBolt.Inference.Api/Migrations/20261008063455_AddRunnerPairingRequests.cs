using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRunnerPairingRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runner_pairing_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    public_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    public_key_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    os = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    arch = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    runner_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    request_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_polled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    denied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_runner_pairing_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_runner_pairing_requests_application_users_approved_by_user_~",
                        column: x => x.approved_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_runner_pairing_requests_organizations_approved_organization~",
                        column: x => x.approved_organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runner_pairing_requests_runner_devices_approved_device_id",
                        column: x => x.approved_device_id,
                        principalTable: "runner_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runner_pairing_requests_approved_by_user_id",
                table: "runner_pairing_requests",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_pairing_requests_approved_device_id",
                table: "runner_pairing_requests",
                column: "approved_device_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_pairing_requests_approved_organization_id",
                table: "runner_pairing_requests",
                column: "approved_organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_runner_pairing_requests_device_code_hash",
                table: "runner_pairing_requests",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runner_pairing_requests_expires_at",
                table: "runner_pairing_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_runner_pairing_requests_user_code_pending",
                table: "runner_pairing_requests",
                column: "user_code",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runner_pairing_requests");
        }
    }
}
