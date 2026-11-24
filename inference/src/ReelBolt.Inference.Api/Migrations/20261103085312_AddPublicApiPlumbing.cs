using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicApiPlumbing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_idempotency_records",
                columns: table => new
                {
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_idempotency_records", x => new { x.api_key_id, x.key });
                });

            migrationBuilder.CreateTable(
                name: "api_key_audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    route = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    request_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_key_rate_counters",
                columns: table => new
                {
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    window_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    writes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_rate_counters", x => new { x.api_key_id, x.window_start });
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_idempotency_records_created_at",
                table: "api_idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_audit_events_api_key_id_occurred_at",
                table: "api_key_audit_events",
                columns: new[] { "api_key_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_audit_events_occurred_at",
                table: "api_key_audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_api_key_audit_events_organization_id_occurred_at",
                table: "api_key_audit_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_api_key_rate_counters_window_start",
                table: "api_key_rate_counters",
                column: "window_start");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_idempotency_records");

            migrationBuilder.DropTable(
                name: "api_key_audit_events");

            migrationBuilder.DropTable(
                name: "api_key_rate_counters");
        }
    }
}
