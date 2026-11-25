using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCspViolationCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "csp_violation_counts",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    report_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    disposition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    effective_directive = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    blocked_origin = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    document_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ua_family = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_csp_violation_counts", x => new { x.day, x.report_type, x.disposition, x.effective_directive, x.blocked_origin, x.document_path, x.ua_family });
                });

            migrationBuilder.CreateIndex(
                name: "ix_csp_violation_counts_day",
                table: "csp_violation_counts",
                column: "day");

            // powerbi_ro reads the table (infra/powerbi/csp-violations.sql). Guarded on the role existing: the
            // operator script creates it, and an unguarded GRANT would fail the migration on a database that has none.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                        GRANT SELECT ON TABLE csp_violation_counts TO powerbi_ro;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "csp_violation_counts");
        }
    }
}
