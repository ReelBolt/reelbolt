namespace ReelBolt.Inference.Api.Data.BiReadOnlyRole;

/// <summary>
/// The raw SQL that creates the <c>powerbi_ro</c> BI-extract login role: its read-only hardening and
/// its grants on the twenty-five Inference-API-owned tables. Kept as named, public constants so a test
/// can read exactly what the operator script (<c>infra/powerbi/postgres-readonly-role.sql</c>) runs
/// against a real PostgreSQL and the guard test can classify every public table.
/// <para>
/// The house pattern is <c>infra/grafana/postgres-readonly-role.sql</c>. The role is <c>LOGIN</c> with
/// no credential: the container's <c>pg_hba.conf</c> trusts the local socket, so the extract connects
/// without a password and no password, key or token is ever committed.
/// </para>
/// <para>
/// The SQL is split by what it needs. <see cref="RoleAndAlterSql"/> creates the role and hardens it -
/// <c>CREATE ROLE</c> and <c>ALTER ROLE</c> need CREATEROLE, so that is the operator script's alone and
/// no migration runs it. <see cref="GrantsSql"/> is the convergent revoke-then-grant block and
/// <see cref="AssertionSql"/> the closing assertion; neither needs CREATEROLE, so the API grant
/// migration runs them too - each inside its own role-existence guard, so a fresh database (role
/// absent) is a no-op and every deploy converges a database that carries an older, wider ACL.
/// <see cref="Up"/> is the three concatenated, which is what the operator script applies verbatim.
/// </para>
/// <para>
/// The hardening block (the revokes, the read-only session default and the closing assertion) is the
/// requirement, not a suggestion; the session default is a clearable convenience, the privilege model
/// is the control. The closing assertion addresses each public table by <c>pg_class.oid</c> and calls
/// the oid overload of <c>has_table_privilege</c>, not the text overload on <c>relname</c>, so a
/// mixed-case table such as <c>__EFMigrationsHistory_Api</c> cannot be folded to lower case and abort
/// the script.
/// </para>
/// <para>
/// The three WorkflowEngine-owned tables (<c>workflow_executions</c>, <c>workflow_step_results</c>,
/// <c>runner_jobs</c>) are NOT named here. The Inference API migrates before the engine, so on a fresh
/// database those tables do not exist yet and naming them would crash every fresh install; the engine
/// block (<c>PowerBiReadOnlyRoleEngineSql</c>) grants them.
/// </para>
/// <para>
/// Maintenance rule: a new public table must be added to <see cref="FullSelectTables"/> or
/// <see cref="ColumnSelectTables"/> here (or, for an engine-owned table, to
/// <c>PowerBiReadOnlyRoleEngineSql.GrantedTables</c>), or classified in
/// <c>PowerBiReadOnlyRoleGuardTests</c>, or the build fails - an unclassified table is invisible to
/// <c>powerbi_ro</c> and breaks the nightly extract silently.
/// </para>
/// </summary>
public static class PowerBiReadOnlyRoleSql
{
    /// <summary>The role the nightly BI extract connects as.</summary>
    public const string RoleName = "powerbi_ro";

    /// <summary>The role's connection cap.</summary>
    public const int ConnectionLimit = 5;

    /// <summary>The API-owned tables granted table-wide <c>SELECT</c>, sorted.</summary>
    public static readonly IReadOnlyList<string> FullSelectTables = new[]
    {
        "billing_payments",
        "billing_refunds",
        "credit_accounts",
        "credit_grants",
        "credit_ledger_entries",
        "inference_models",
        "marketing_link_daily_stats",
        "marketing_links",
        "organization_memberships",
        "organizations",
        "plans",
        "pricing_settings",
        "product_events",
        "projects",
        "rate_cards",
        "rate_multipliers",
        "runner_devices",
        "subscriptions",
        "usage_events",
        "user_attribution",
        "user_onboarding_profiles",
        "visitor_attribution"
    };

    /// <summary>
    /// The API-owned tables granted column-by-column, each mapped to exactly the columns granted. The
    /// excluded column of each is a credential or run output that must not reach the extract.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ColumnSelectTables =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["agent_definitions"] = new[]
            {
                "agent_type",
                "assigned_skills_json",
                "available_tools_json",
                "color",
                "context_mode",
                "context_window_size",
                "created_at",
                "description",
                "generates_output",
                "id",
                "inference_provider_id",
                "is_built_in",
                "name",
                "organization_id",
                "output_schema_json",
                "output_schema_name",
                "owner_id",
                "reasoning_effort"
            },
            ["inference_providers"] = new[]
            {
                "api_key_last_four",
                "created_at",
                "endpoint",
                "id",
                "is_enabled",
                "is_system",
                "kind",
                "name",
                "organization_id",
                "personal_oauth_mode",
                "timeout_seconds",
                "updated_at"
            },
            ["application_users"] = new[]
            {
                "assistant_auto_approve",
                "auth_provider",
                "created_at",
                "deletion_requested_at",
                "display_name",
                "email",
                "email_verified_at",
                "id",
                "is_admin",
                "is_service_account",
                "last_active_organization_id",
                "locale",
                "mcp_admin_tools_enabled",
                "mcp_media_egress_enabled",
                "must_change_password",
                "tokens_invalidated_at"
            }
        };

    /// <summary>
    /// The nineteen sensitive columns never granted to the role, on any table. The list spans both
    /// contexts: <c>agent_definitions.system_prompt</c> and <c>agent_definitions.config_json</c> here
    /// and <c>workflow_executions.agent_snapshot_json</c> (the run's frozen agent snapshot, which
    /// serializes each <c>AgentSnapshotEntry.SystemPrompt</c> verbatim) and
    /// <c>workflow_executions.style_config_json</c> on the engine side, the
    /// credential columns of <c>inference_providers</c> and <c>application_users</c> (<c>signup_ip</c>
    /// is PII the BI model has no use for), the two raw-error-body columns
    /// <c>workflow_executions.error_message</c> and <c>workflow_step_results.error_details</c>, whose
    /// text comes from <c>ex.Message</c> and can carry a bounded vendor error body that
    /// <c>ImagePayload.Bounded</c> does not actually redact, and the eight <c>workflow_executions</c> /
    /// <c>workflow_step_results</c> columns whose content is arbitrary caller, model or tool text:
    /// <c>tool_calls_json</c> (serialized tool names, arguments and results - <c>ReadProjectFile</c>
    /// returns file contents, a provider-test tool takes provider fields), <c>reasoning_json</c> (the
    /// model's private reasoning, which can restate the system prompt the model is shown as chat
    /// instructions), the prose columns <c>input_json</c>, <c>output</c>, <c>output_json</c>,
    /// <c>chat_transcript_json</c>, <c>result_json</c> and <c>user_request</c>, and the last two
    /// caller-authored free-text columns: <c>style_config_json</c> (carries a
    /// <c>StyleConfig.Notes</c> the user writes freely; the sanitizer that touches it is a
    /// prompt-injection defense, not a redactor) and <c>config_json</c> (set verbatim from a
    /// caller-supplied request), plus <c>workflow_executions.run_brief_json</c>, the stored part of
    /// the run brief (its notes are free text and it names the caller's files and voice), withheld
    /// like the request text it travels with. The prose argument is
    /// the same one: <c>input_json</c> composes the caller's request, and a caller can paste a secret
    /// into <c>user_request</c>; the model then sees it and can restate it in <c>output</c>,
    /// <c>output_json</c>, <c>chat_transcript_json</c> and <c>result_json</c>. That cannot be proven
    /// secret-free, so none of them is granted. What remains is the metadata - storage keys,
    /// <c>failure_class</c>/<c>failure_code</c>, tokens/durations/status - which is the BI value that
    /// survives.
    /// </summary>
    public static readonly IReadOnlyList<string> SensitiveColumnsNotGranted = new[]
    {
        "system_prompt",
        "agent_snapshot_json",
        "error_message",
        "error_details",
        "tool_calls_json",
        "reasoning_json",
        "signup_ip",
        "api_key_encrypted",
        "extra_headers_json",
        "password_hash",
        "chat_transcript_json",
        "input_json",
        "output_json",
        "output",
        "result_json",
        "user_request",
        "style_config_json",
        // The run brief's stored part (language, length, voice and source-file ids): withheld like the run's request text.
        "run_brief_json",
        "config_json"
    };

    /// <summary>
    /// RoleAndAlterSql: create the role (idempotent, so an operator who pre-created it is not an
    /// error), cap its connections and default it to read-only. <c>CREATE ROLE</c> and <c>ALTER ROLE</c>
    /// need CREATEROLE, so this is the operator script's alone: no migration runs it, because every
    /// migration runs as a principal that is LOGIN CREATEDB NOSUPERUSER by contract and would abort
    /// with 42501.
    /// </summary>
    public const string RoleAndAlterSql =
        @"DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    CREATE ROLE powerbi_ro LOGIN;
  END IF;
END $$;
ALTER ROLE powerbi_ro CONNECTION LIMIT 5;
ALTER ROLE powerbi_ro SET default_transaction_read_only = on;";

    /// <summary>
    /// GrantsSql: the convergent revoke-then-grant block, guarded on the role existing so a fresh
    /// database (role absent) is a no-op. It names the sensitive columns in explicit column-level
    /// <c>REVOKE</c>s (belt-and-braces: the table-level <c>REVOKE</c> below is the mechanism, and on
    /// PostgreSQL 16 already clears column grants), clears any table-level <c>SELECT</c> an earlier
    /// revision of this grant left on the eighteen tables, grants CONNECT and USAGE, grants SELECT on
    /// the sixteen table-wide tables and the named columns of the three partial tables, then revokes
    /// every write privilege from the role and from PUBLIC. It needs no CREATEROLE, so the API grant
    /// migration runs it as well as the operator script. The leading REVOKE is what makes the statement
    /// set convergent: a database that applied an earlier, wider revision of this grant keeps the stale
    /// table-level entries, and they would otherwise shadow the narrowed column grants. The two tables
    /// created <em>after</em> this grant (<c>billing_payments</c>, <c>product_events</c>) each carry
    /// their own guarded <c>REVOKE</c>/<c>GRANT</c> pair, for the same convergence reason.
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

    -- The new-user funnel tables (docs/design/new-user-funnel.md) are created by one migration that sorts
    -- after this grant: same guard and same leading REVOKE as product_events above, one block per table.
    -- marketing_links is the tracked-link registry and marketing_link_daily_stats its click counter (a
    -- count per link and day: no visitor, IP address or user agent); visitor_attribution is the
    -- first-touch source of a consented anonymous visitor; user_onboarding_profiles is the persona and
    -- material a person chose. Every column is a closed token, a timestamp, a count or an id, except
    -- marketing_links.placement, the operator's own note on where the link is posted (no customer text).
    IF to_regclass('public.marketing_links') IS NOT NULL THEN
      REVOKE SELECT ON marketing_links FROM powerbi_ro;
      GRANT SELECT ON marketing_links TO powerbi_ro;
    END IF;
    IF to_regclass('public.marketing_link_daily_stats') IS NOT NULL THEN
      REVOKE SELECT ON marketing_link_daily_stats FROM powerbi_ro;
      GRANT SELECT ON marketing_link_daily_stats TO powerbi_ro;
    END IF;
    IF to_regclass('public.visitor_attribution') IS NOT NULL THEN
      REVOKE SELECT ON visitor_attribution FROM powerbi_ro;
      GRANT SELECT ON visitor_attribution TO powerbi_ro;
    END IF;
    IF to_regclass('public.user_onboarding_profiles') IS NOT NULL THEN
      REVOKE SELECT ON user_onboarding_profiles FROM powerbi_ro;
      GRANT SELECT ON user_onboarding_profiles TO powerbi_ro;
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
      last_active_organization_id, locale, mcp_admin_tools_enabled, mcp_media_egress_enabled,
      must_change_password, tokens_invalidated_at)
      ON application_users TO powerbi_ro;

    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM powerbi_ro;
    REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER
      ON ALL TABLES IN SCHEMA public FROM PUBLIC;
  END IF;
END $$;";

    /// <summary>
    /// AssertionSql: the closing assertion, identical in shape to grafana_ro's, over every public
    /// table. Guarded on the role existing - the oid overload of <c>has_table_privilege</c> raises if
    /// the role is absent, so the grant migration can run it on a fresh database as a no-op. It walks
    /// <c>pg_class.oid</c>, not <c>relname</c>: the oid overload does no name parsing, so a mixed-case
    /// table (<c>__EFMigrationsHistory_Api</c>) cannot be folded to lower case and raise 42P01.
    /// </summary>
    public const string AssertionSql =
        @"DO $$
DECLARE o oid; p text;
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    FOR o IN
      SELECT c.oid FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = 'public' AND c.relkind IN ('r','p')
    LOOP
      FOREACH p IN ARRAY ARRAY['INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER'] LOOP
        IF has_table_privilege('powerbi_ro', o, p) THEN
          RAISE EXCEPTION 'powerbi_ro holds % on %; the BI extract role must be read-only', p, o::regclass;
        END IF;
      END LOOP;
    END LOOP;
  END IF;
END $$;";

    /// <summary>
    /// ServiceAccountColumnGrantSql: grants the BI role the <c>application_users.is_service_account</c>
    /// column so the model can exclude API-key service accounts from user counts. Guarded on the role
    /// existing. A migration that adds the column calls this from its Up (the registry block is not
    /// re-run by later migrations).
    /// </summary>
    public const string ServiceAccountColumnGrantSql =
        @"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='powerbi_ro') THEN GRANT SELECT (is_service_account) ON application_users TO powerbi_ro; END IF; END $$;";

    /// <summary>
    /// Up: the operator script's block, applied verbatim - create and harden the role, converge the
    /// grants, then assert the privilege model holds. The grant migration runs the last two parts
    /// (<see cref="GrantsSql"/> then <see cref="AssertionSql"/>) alone: it must not create or alter a
    /// role, and each part is already guarded on the role existing.
    /// </summary>
    public const string Up = RoleAndAlterSql + "\n" + GrantsSql + "\n" + AssertionSql;

    /// <summary>
    /// Down: drop the role and everything it owns, only if it still exists. Forward-only in practice -
    /// rolling the code back does not roll the role back.
    /// </summary>
    public const string Down =
        @"DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    EXECUTE 'DROP OWNED BY powerbi_ro';
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM powerbi_ro', current_database());
    EXECUTE 'DROP ROLE powerbi_ro';
  END IF;
END $$;";
}
