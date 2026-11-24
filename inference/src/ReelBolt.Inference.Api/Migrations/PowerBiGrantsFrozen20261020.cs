namespace ReelBolt.Inference.Api.Migrations;

/// <summary>
/// The grant block exactly as it stood when
/// <see cref="AddPowerBiReadOnlyRoleGrants"/> was created (commit <c>d116462c2</c>), frozen here so
/// the migration never references a column another migration adds later. A fresh database with the
/// cluster-wide <c>powerbi_ro</c> role present runs this migration before those columns exist, so a
/// reference to the live <c>PowerBiReadOnlyRoleSql.GrantsSql</c> (which grants <c>locale</c> and
/// <c>is_service_account</c>) aborted with 42703. The head ACL is converged by
/// <see cref="ConvergePowerBiReadOnlyRoleGrants"/>; the live constant describes the operator
/// script's current state.
/// </summary>
internal static class PowerBiGrantsFrozen20261020
{
    /// <summary>
    /// <c>PowerBiReadOnlyRoleSql.GrantsSql</c> at <c>d116462c2</c>, verbatim. Do not refresh it: a
    /// column added after 2026-10-20 must not appear here.
    /// </summary>
    public const string GrantsSql =
        @"DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    -- belt-and-braces: name every column any revision of this grant ever granted and the current one
    -- withholds, so the intent (clear anything a revision granted) is stated explicitly. This is NOT
    -- the mechanism: a table-level REVOKE SELECT ON t FROM r already clears column-level grants on
    -- PostgreSQL 16 (verified empirically - after GRANT SELECT (a) a table-level REVOKE leaves attacl
    -- empty and has_column_privilege false). The table-level REVOKE below does the work; these lines
    -- are idempotent no-ops kept for intent, not load-bearing.
    REVOKE SELECT (system_prompt) ON agent_definitions FROM powerbi_ro;
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

    GRANT SELECT (agent_type, assigned_skills_json, available_tools_json, color, config_json,
      context_mode, context_window_size, created_at, description, generates_output, id,
      inference_provider_id, is_built_in, name, organization_id, output_schema_json,
      output_schema_name, owner_id, reasoning_effort) ON agent_definitions TO powerbi_ro;
    GRANT SELECT (api_key_last_four, created_at, endpoint, id, is_enabled, is_system, kind,
      name, organization_id, personal_oauth_mode, timeout_seconds, updated_at)
      ON inference_providers TO powerbi_ro;
    GRANT SELECT (assistant_auto_approve, auth_provider, created_at, deletion_requested_at,
      display_name, email, email_verified_at, id, is_admin, last_active_organization_id,
      mcp_admin_tools_enabled, must_change_password, tokens_invalidated_at)
      ON application_users TO powerbi_ro;

    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM powerbi_ro;
    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM PUBLIC;
  END IF;
END $$;";
}
