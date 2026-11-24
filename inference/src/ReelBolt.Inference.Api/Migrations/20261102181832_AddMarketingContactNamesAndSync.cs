using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketingContactNamesAndSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "brevo_push_attempted_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "brevo_push_attempts",
                table: "marketing_consents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "brevo_push_error_code",
                table: "marketing_consents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "brevo_pushed_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_name",
                table: "marketing_consents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_name",
                table: "marketing_consents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_source",
                table: "marketing_consents",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "name_updated_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketing_contact_sync_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    last_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_run_ok = table.Column<bool>(type: "boolean", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_reconcile_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    run_requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_contact_sync_states", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketing_contact_sync_state");

            migrationBuilder.DropColumn(
                name: "brevo_push_attempted_at",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "brevo_push_attempts",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "brevo_push_error_code",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "brevo_pushed_at",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "first_name",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "last_name",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "name_source",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "name_updated_at",
                table: "marketing_consents");
        }
    }
}
