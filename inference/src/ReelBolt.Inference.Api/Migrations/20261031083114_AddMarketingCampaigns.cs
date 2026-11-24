using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketingCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "brevo_synced_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "undeliverable_at",
                table: "marketing_consents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "undeliverable_reason",
                table: "marketing_consents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketing_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    html = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    test_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    test_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    confirmed_recipient_count = table.Column<int>(type: "integer", nullable: true),
                    final_recipient_count = table.Column<int>(type: "integer", nullable: true),
                    brevo_campaign_id = table.Column<long>(type: "bigint", nullable: true),
                    dispatch_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    stats_json = table.Column<string>(type: "text", nullable: true),
                    stats_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_campaigns", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_marketing_campaigns_created_at",
                table: "marketing_campaigns",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_marketing_campaigns_status_created_at",
                table: "marketing_campaigns",
                columns: new[] { "status", "created_at" });

            // At most one active campaign: a unique index on a constant, over the active rows only.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_marketing_campaigns_one_active ON marketing_campaigns ((1)) "
                + "WHERE status IN ('Queued', 'Syncing', 'Sending');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketing_campaigns");

            migrationBuilder.DropColumn(
                name: "brevo_synced_at",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "undeliverable_at",
                table: "marketing_consents");

            migrationBuilder.DropColumn(
                name: "undeliverable_reason",
                table: "marketing_consents");
        }
    }
}
