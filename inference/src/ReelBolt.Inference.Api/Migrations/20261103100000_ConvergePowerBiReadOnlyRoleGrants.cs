using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;

// contract-migration(HelperSql): grant convergence only, no schema contraction; SQL is a frozen copy of the head grant set

#nullable disable

namespace ReelBolt.Inference.Api.Migrations
{
    /// <summary>
    /// Converges the <c>powerbi_ro</c> ACL to the head state on a database that was migrated from
    /// zero. The original grant migration (<see cref="AddPowerBiReadOnlyRoleGrants"/>) is frozen to
    /// the text it had when it was created, because a fresh database with the cluster-wide role
    /// present runs it before later columns exist; this migration, at head, re-applies the current
    /// grant set so those columns (<c>application_users.is_service_account</c>,
    /// <c>application_users.locale</c>, ...) are converged and <c>agent_definitions.config_json</c> is
    /// withdrawn again.
    /// <para>
    /// The SQL below is a frozen copy of <c>PowerBiReadOnlyRoleSql.GrantsSql</c> at the time this
    /// migration was written, not a reference: a migration must describe the schema state it ran
    /// against, or a later edit to the registry constant changes history. The closing assertion is
    /// the shared <c>AssertionSql</c>, which walks <c>pg_class</c> only and names no column, so it
    /// cannot depend on a later column and is safe to reference.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public partial class ConvergePowerBiReadOnlyRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(HeadGrantsSql);
            migrationBuilder.Sql(PowerBiReadOnlyRoleSql.AssertionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No inverse. This migration converges the ACL to the head grant set; the frozen
            // migration before it is deliberately narrower in some respects, and rolling back to a
            // wider- or narrower-than-intended ACL is never wanted. Role lifecycle belongs to the
            // operator script alone.
        }

        /// <summary>
        /// <c>PowerBiReadOnlyRoleSql.GrantsSql</c> copied verbatim at the time of writing. Do not
        /// refresh it.
        /// </summary>
        private const string HeadGrantsSql =
            @"DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    -- belt-and-braces: name every column any revision of this grant ever granted and the current one
    -- withholds, so the intent (clear anything a revision granted) is stated explicitly. This is NOT
    -- the mechanism: a table-level REVOKE SELECT ON t FROM r already clears column-level grants on
    -- PostgreSQL 16 (verified empirically - after GRANT SELECT (a) a table-level REVOKE leaves attacl
    -- empty and has_column_privilege false). The table-level REVOKE below does the work; these lines
    -- are idempotent no-ops kept for intent, not load-bearing.
    REVOKE SELECT (system_prompt, config_json) ON agent_definitions FROM powerbi_ro;
    REVOKE SELECT (api_key_encrypted, extra_headers_json) ON inference_providers FROM powerbi_ro;
    REVOKE SELECT (password_hash, signup_ip) ON application_users FROM powerbi_ro;

    -- clear table-level SELECT left by an earlier revision of this grant, so a re-run normalizes.
    REVOKE SELECT ON usage_events, credit_ledger_entries, credit_accounts, credit_grants,
      subscriptions, plans, rate_cards, rate_multipliers, pricing_settings, organizations,
      organization_memberships, projects, runner_devices, inference_models, agent_definitions,
      inference_providers, application_users FROM powerbi_ro;

    -- the database name is only known at run time, so CONNECT goes through EXECUTE: a nested DO block
    -- is not allowed inside this one.
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO powerbi_ro', current_database());
    GRANT USAGE ON SCHEMA public TO powerbi_ro;

    GRANT SELECT ON usage_events, credit_ledger_entries, credit_accounts, credit_grants,
      subscriptions, plans, rate_cards, rate_multipliers, pricing_settings, organizations,
      organization_memberships, projects, runner_devices, inference_models TO powerbi_ro;

    -- billing_payments (the money the provider actually took) is created by a migration that sorts
    -- after this grant, and the operator script may be applied to a database older than that
    -- migration, so it is granted only when it exists. A bare name in the list above would abort the
    -- whole block - and with it the deploy - on such a database.
    --
    -- The REVOKE is what keeps it convergent: the leading table-level REVOKE above does not name this
    -- table, so without one here a database carrying a wider revision of this grant would keep it and
    -- a later narrowing would silently converge nothing.
    IF to_regclass('public.billing_payments') IS NOT NULL THEN
      REVOKE SELECT ON billing_payments FROM powerbi_ro;
      GRANT SELECT ON billing_payments TO powerbi_ro;
    END IF;

    -- billing_refunds (approved refunds and chargebacks) is created by a migration that sorts after this
    -- grant: same guard and same leading REVOKE as billing_payments above.
    IF to_regclass('public.billing_refunds') IS NOT NULL THEN
      REVOKE SELECT ON billing_refunds FROM powerbi_ro;
      GRANT SELECT ON billing_refunds TO powerbi_ro;
    END IF;

    -- product_events (the product funnel the BI model reads as the authoritative funnel) is created by
    -- a migration that sorts after this grant, for the same reason and with the same guard as
    -- billing_payments above: a database older than that migration must not abort the whole block. Its
    -- own leading REVOKE is what keeps the narrowing convergent - the table-level REVOKE above does not
    -- name it, so without one here a database carrying a wider revision would keep it.
    IF to_regclass('public.product_events') IS NOT NULL THEN
      REVOKE SELECT ON product_events FROM powerbi_ro;
      GRANT SELECT ON product_events TO powerbi_ro;
    END IF;

    -- user_attribution (first-touch acquisition source per user, product-analytics) is created by a
    -- migration that sorts after this grant: same guard and same leading REVOKE as product_events above.
    IF to_regclass('public.user_attribution') IS NOT NULL THEN
      REVOKE SELECT ON user_attribution FROM powerbi_ro;
      GRANT SELECT ON user_attribution TO powerbi_ro;
    END IF;

    GRANT SELECT (agent_type, assigned_skills_json, available_tools_json, color,
      context_mode, context_window_size, created_at, description, generates_output, id,
      inference_provider_id, is_built_in, name, organization_id, output_schema_json,
      output_schema_name, owner_id, reasoning_effort) ON agent_definitions TO powerbi_ro;
    GRANT SELECT (api_key_last_four, created_at, endpoint, id, is_enabled, is_system, kind,
      name, organization_id, personal_oauth_mode, timeout_seconds, updated_at)
      ON inference_providers TO powerbi_ro;
    GRANT SELECT (assistant_auto_approve, auth_provider, created_at, deletion_requested_at,
      display_name, email, email_verified_at, id, is_admin, is_service_account,
      last_active_organization_id, locale, mcp_admin_tools_enabled, must_change_password,
      tokens_invalidated_at)
      ON application_users TO powerbi_ro;

    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM powerbi_ro;
    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM PUBLIC;
  END IF;
END $$;";
    }
}
