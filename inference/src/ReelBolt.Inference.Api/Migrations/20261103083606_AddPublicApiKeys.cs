using System;
using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;

// contract-migration(HelperSql): expand only, creates the public API key tables

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_service_account",
                table: "application_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_four = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    project_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    daily_cap_millis = table.Column<long>(type: "bigint", nullable: true),
                    weekly_cap_millis = table.Column<long>(type: "bigint", nullable: true),
                    monthly_cap_millis = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiry_warned_7d_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expiry_warned_1d_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_application_users_service_user_id",
                        column: x => x.service_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_api_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_id",
                table: "api_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_organization_id_revoked_at",
                table: "api_keys",
                columns: new[] { "organization_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_service_user_id",
                table: "api_keys",
                column: "service_user_id",
                unique: true);

            migrationBuilder.Sql(PowerBiReadOnlyRoleSql.ServiceAccountColumnGrantSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropColumn(
                name: "is_service_account",
                table: "application_users");
            // No grant revoke: dropping the column drops its column-level grant with it.
        }
    }
}
