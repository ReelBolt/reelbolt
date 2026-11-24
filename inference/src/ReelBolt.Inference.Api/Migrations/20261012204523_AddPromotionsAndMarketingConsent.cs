using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionsAndMarketingConsent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_credit_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credits_millis = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_credit_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_admin_credit_grants_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketing_consent_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_consent_exports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_consents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    wording_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    consented_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_confirmation_sent_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_consents", x => x.id);
                    table.ForeignKey(
                        name: "FK_marketing_consents_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "marketing_suppressions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_hmac = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    withdrawn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_suppressions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    public_headline = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_redemptions = table.Column<int>(type: "integer", nullable: true),
                    redemption_count = table.Column<int>(type: "integer", nullable: false),
                    max_redemptions_per_org = table.Column<int>(type: "integer", nullable: false),
                    credits_millis = table.Column<long>(type: "bigint", nullable: true),
                    credit_expiry_days = table.Column<int>(type: "integer", nullable: true),
                    discount_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    discount_amount = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    duration_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    duration_months = table.Column<int>(type: "integer", nullable: true),
                    target_plan_keys_json = table.Column<string>(type: "jsonb", nullable: false),
                    target_pack_credits_json = table.Column<string>(type: "jsonb", nullable: false),
                    target_price_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    bonus_percent = table.Column<int>(type: "integer", nullable: true),
                    provider_discount_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_consent_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    wording_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_consent_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_marketing_consent_events_marketing_consents_consent_id",
                        column: x => x.consent_id,
                        principalTable: "marketing_consents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "promotion_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credits_millis = table.Column<long>(type: "bigint", nullable: true),
                    discount_total = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_redemptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_promotion_redemptions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_promotion_redemptions_promotions_promotion_id",
                        column: x => x.promotion_id,
                        principalTable: "promotions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_credit_grants_organization_id_created_at",
                table: "admin_credit_grants",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_credit_grants_request_id",
                table: "admin_credit_grants",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketing_consent_events_consent_id_occurred_at",
                table: "marketing_consent_events",
                columns: new[] { "consent_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_marketing_consent_exports_exported_at",
                table: "marketing_consent_exports",
                column: "exported_at");

            migrationBuilder.CreateIndex(
                name: "IX_marketing_consents_email",
                table: "marketing_consents",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketing_consents_status_requested_at",
                table: "marketing_consents",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_marketing_consents_user_id",
                table: "marketing_consents",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_marketing_suppressions_email_hmac",
                table: "marketing_suppressions",
                column: "email_hmac",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_organization_id",
                table: "promotion_redemptions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_promotion_id_organization_id",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_redemptions_promotion_id_provider_transaction_id",
                table: "promotion_redemptions",
                columns: new[] { "promotion_id", "provider_transaction_id" },
                unique: true,
                filter: "provider_transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_promotions_code",
                table: "promotions",
                column: "code",
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_promotions_kind_status",
                table: "promotions",
                columns: new[] { "kind", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_promotions_provider_discount_id",
                table: "promotions",
                column: "provider_discount_id");

            // Privacy version bump: the policy now covers marketing consent. Only the shipped
            // default moves; an operator-published version is never touched, and a fresh install
            // gets the new default from LegalDocumentSeeder instead.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10-12', effective_at = now(), updated_at = now(), " +
                "updated_by = 'migration:AddPromotionsAndMarketingConsent' " +
                "WHERE document = 'Privacy' AND version = '2026-10';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo only the bump this migration made itself.
            migrationBuilder.Sql(
                "UPDATE legal_documents SET version = '2026-10', updated_at = now(), " +
                "updated_by = 'migration:AddPromotionsAndMarketingConsent:down' " +
                "WHERE document = 'Privacy' AND version = '2026-10-12' " +
                "AND updated_by = 'migration:AddPromotionsAndMarketingConsent';");

            migrationBuilder.DropTable(
                name: "admin_credit_grants");

            migrationBuilder.DropTable(
                name: "marketing_consent_events");

            migrationBuilder.DropTable(
                name: "marketing_consent_exports");

            migrationBuilder.DropTable(
                name: "marketing_suppressions");

            migrationBuilder.DropTable(
                name: "promotion_redemptions");

            migrationBuilder.DropTable(
                name: "marketing_consents");

            migrationBuilder.DropTable(
                name: "promotions");
        }
    }
}
