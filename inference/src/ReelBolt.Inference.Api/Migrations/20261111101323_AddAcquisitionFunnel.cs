using System;
using Microsoft.EntityFrameworkCore.Migrations;

// contract-migration(AddCheckConstraint): expand only. The one CHECK added to an existing table is on user_attribution.capture, a nullable column this same migration adds: every existing row is NULL and no released writer sets the column, so the old release cannot violate it.

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// The new-user funnel schema (docs/design/new-user-funnel.md section 5), expand only: the tracked links
    /// and their daily click counters, first-touch attribution of consented visitors, the per-user
    /// onboarding profile, and <c>user_attribution.capture</c>. The four new tables are granted to the BI
    /// role table-wide when that role exists (the <c>product_events</c> precedent); the SQL is a literal
    /// here, not a reference to the live registry, so this migration stays frozen.
    /// </summary>
    public partial class AddAcquisitionFunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "capture",
                table: "user_attribution",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "marketing_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    medium = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    campaign = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    placement = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    destination_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    persona = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cost_minor = table.Column<long>(type: "bigint", nullable: true),
                    cost_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archived_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_links", x => x.id);
                    table.CheckConstraint("ck_marketing_links_channel", "channel IN ('hackernews', 'reddit', 'linkedin', 'x', 'producthunt', 'youtube', 'tiktok', 'instagram', 'facebook', 'github', 'discord', 'newsletter', 'email', 'blog', 'directory', 'press', 'partner', 'ads', 'other')");
                    table.CheckConstraint("ck_marketing_links_cost_pair", "(cost_minor IS NULL) = (cost_currency IS NULL) AND (cost_minor IS NULL OR cost_minor >= 0)");
                    table.CheckConstraint("ck_marketing_links_destination_path", "destination_path LIKE '/%' AND destination_path NOT LIKE '%//%'");
                    table.CheckConstraint("ck_marketing_links_medium", "medium IN ('social', 'community', 'video', 'email', 'referral', 'paid', 'owned', 'press')");
                    table.CheckConstraint("ck_marketing_links_persona", "persona IS NULL OR persona IN ('creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other')");
                    table.ForeignKey(
                        name: "FK_marketing_links_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_onboarding_profiles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    persona = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    material = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    suggested_persona = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    answered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    skipped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_onboarding_profiles", x => x.user_id);
                    table.CheckConstraint("ck_user_onboarding_profiles_material", "material IS NULL OR material IN ('footage', 'code', 'idea')");
                    table.CheckConstraint("ck_user_onboarding_profiles_persona", "persona IS NULL OR persona IN ('creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other')");
                    table.CheckConstraint("ck_user_onboarding_profiles_suggested_persona", "suggested_persona IS NULL OR suggested_persona IN ('creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other')");
                    table.ForeignKey(
                        name: "FK_user_onboarding_profiles_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visitor_attribution",
                columns: table => new
                {
                    visitor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    utm_source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_medium = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_campaign = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_content = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    utm_term = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    referrer_host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    landing_path = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    first_touch_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_visitor_attribution", x => x.visitor_id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_link_daily_stats",
                columns: table => new
                {
                    link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    clicks = table.Column<int>(type: "integer", nullable: false),
                    bot_hits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_link_daily_stats", x => new { x.link_id, x.day });
                    table.CheckConstraint("ck_marketing_link_daily_stats_non_negative", "clicks >= 0 AND bot_hits >= 0");
                    table.ForeignKey(
                        name: "FK_marketing_link_daily_stats_marketing_links_link_id",
                        column: x => x.link_id,
                        principalTable: "marketing_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_user_attribution_capture",
                table: "user_attribution",
                sql: "capture IS NULL OR capture IN ('cookie', 'url')");

            migrationBuilder.CreateIndex(
                name: "IX_marketing_links_created_by_user_id",
                table: "marketing_links",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_marketing_links_code",
                table: "marketing_links",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_visitor_attribution_first_touch_at",
                table: "visitor_attribution",
                column: "first_touch_at");

            migrationBuilder.CreateIndex(
                name: "ix_visitor_attribution_utm_content_first_touch_at",
                table: "visitor_attribution",
                columns: new[] { "utm_content", "first_touch_at" },
                filter: "utm_content IS NOT NULL");

            // powerbi_ro reads the four tables table-wide. Guarded on the role existing: the operator script
            // creates it, and an unguarded GRANT would fail the migration on a database that has none.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
                        GRANT SELECT ON TABLE marketing_links, marketing_link_daily_stats, visitor_attribution, user_onboarding_profiles TO powerbi_ro;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketing_link_daily_stats");

            migrationBuilder.DropTable(
                name: "user_onboarding_profiles");

            migrationBuilder.DropTable(
                name: "visitor_attribution");

            migrationBuilder.DropTable(
                name: "marketing_links");

            migrationBuilder.DropCheckConstraint(
                name: "ck_user_attribution_capture",
                table: "user_attribution");

            migrationBuilder.DropColumn(
                name: "capture",
                table: "user_attribution");
        }
    }
}
