using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_product_events_subject",
                table: "product_events");

            migrationBuilder.AddColumn<string>(
                name: "detail",
                table: "product_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "visitor_id",
                table: "product_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_attribution",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    utm_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_medium = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_campaign = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_content = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_term = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    referrer_host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    landing_path = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_touch_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_attribution", x => x.user_id);
                    table.ForeignKey(
                        name: "FK_user_attribution_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_events_subject",
                table: "product_events",
                sql: "user_id IS NOT NULL OR organization_id IS NOT NULL OR visitor_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_product_events_visitor_event_occurred_at",
                table: "product_events",
                columns: new[] { "visitor_id", "event", "occurred_at" },
                filter: "visitor_id IS NOT NULL");

            migrationBuilder.Sql(ProductEventsMigrationSql.UserAttributionPowerBiGrant);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_product_events_visitor_event_occurred_at",
                table: "product_events");

            migrationBuilder.DropTable(
                name: "user_attribution");

            migrationBuilder.DropCheckConstraint(
                name: "ck_product_events_subject",
                table: "product_events");

            migrationBuilder.DropColumn(
                name: "detail",
                table: "product_events");

            migrationBuilder.DropColumn(
                name: "visitor_id",
                table: "product_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_product_events_subject",
                table: "product_events",
                sql: "user_id IS NOT NULL OR organization_id IS NOT NULL");
        }
    }
}
