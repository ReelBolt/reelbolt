using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider_customer_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_customers", x => x.id);
                    table.ForeignKey(
                        name: "FK_billing_customers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "billing_webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_billing_webhook_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "credit_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance_millis = table.Column<long>(type: "bigint", nullable: false),
                    reserved_millis = table.Column<long>(type: "bigint", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_accounts", x => x.id);
                    table.ForeignKey(
                        name: "FK_credit_accounts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    price_monthly_usd = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    min_seats = table.Column<int>(type: "integer", nullable: true),
                    monthly_credits_millis = table.Column<long>(type: "bigint", nullable: false),
                    one_time_credits_millis = table.Column<long>(type: "bigint", nullable: false),
                    top_up_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    entitlements_json = table.Column<string>(type: "jsonb", nullable: false),
                    provider_price_ids_json = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "trial_claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trial_claims", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "credit_grants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    amount_millis = table.Column<long>(type: "bigint", nullable: false),
                    remaining_millis = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expired_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_credit_grants_credit_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "credit_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credit_ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    balance_delta_millis = table.Column<long>(type: "bigint", nullable: false),
                    reserved_delta_millis = table.Column<long>(type: "bigint", nullable: false),
                    balance_after_millis = table.Column<long>(type: "bigint", nullable: false),
                    ref_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    step_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allocations_json = table.Column<string>(type: "jsonb", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_ledger_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_credit_ledger_entries_credit_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "credit_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    provider_subscription_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    period_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    period_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_subscriptions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_subscriptions_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_customers_organization_id_provider",
                table: "billing_customers",
                columns: new[] { "organization_id", "provider" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_customers_provider_provider_customer_id",
                table: "billing_customers",
                columns: new[] { "provider", "provider_customer_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_billing_webhook_events_processed_at",
                table: "billing_webhook_events",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "IX_billing_webhook_events_provider_event_id",
                table: "billing_webhook_events",
                columns: new[] { "provider", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_accounts_organization_id",
                table: "credit_accounts",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_grants_account_id_expires_at",
                table: "credit_grants",
                columns: new[] { "account_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_account_id_created_at",
                table: "credit_ledger_entries",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_execution_id",
                table: "credit_ledger_entries",
                column: "execution_id");

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_idempotency_key",
                table: "credit_ledger_entries",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_credit_ledger_entries_ref_entry_id",
                table: "credit_ledger_entries",
                column: "ref_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_plans_key",
                table: "plans",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_organization_id_status",
                table: "subscriptions",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_plan_id",
                table: "subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_provider_provider_subscription_id",
                table: "subscriptions",
                columns: new[] { "provider", "provider_subscription_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trial_claims_email_sha256",
                table: "trial_claims",
                column: "email_sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_customers");

            migrationBuilder.DropTable(
                name: "billing_webhook_events");

            migrationBuilder.DropTable(
                name: "credit_grants");

            migrationBuilder.DropTable(
                name: "credit_ledger_entries");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "trial_claims");

            migrationBuilder.DropTable(
                name: "credit_accounts");

            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
