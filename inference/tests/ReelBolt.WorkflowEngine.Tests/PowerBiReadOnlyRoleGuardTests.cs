using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The build-time guard for the <c>powerbi_ro</c> BI-extract role (initiative
/// <c>powerbi-readonly-role</c>). There is no <c>ALTER DEFAULT PRIVILEGES</c> in this database, so a
/// table added by a future migration is invisible to <c>powerbi_ro</c> until someone remembers to
/// grant it - the BI model then breaks silently weeks later with a permissions error that looks like
/// a bug. This test converts that time bomb into a red build: every table in the union of both EF
/// models must be either granted (by <see cref="PowerBiReadOnlyRoleSql"/> or
/// <see cref="PowerBiReadOnlyRoleEngineSql"/>) or classified here in <see cref="Excluded"/> with a
/// reason.
/// <para>
/// The construction is the one <see cref="ApiMigrationOwnershipGuardTests"/> uses: a model-only
/// context, the distinct <c>GetTableName()</c> of its entity types.
/// </para>
/// </summary>
public class PowerBiReadOnlyRoleGuardTests
{
    /// <summary>
    /// Every table in the union of both EF models that is deliberately not granted to
    /// <c>powerbi_ro</c>, mapped to the one-line reason. <see cref="Every_public_table_is_either_granted_to_powerbi_ro_or_explicitly_excluded"/>
    /// asserts this covers <c>all - granted</c> exactly: no unclassified table (the extract would
    /// silently lose it) and no stale entry (a table here that no longer exists, or that is now
    /// granted).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- customer-chosen labels: not in the BI extract scope ---
            ["project_tags"] = "customer-chosen labels; not in the BI extract scope",
            ["project_tag_links"] = "customer-chosen labels; not in the BI extract scope",
            ["project_folders"] = "customer-chosen labels; not in the BI extract scope",
            // --- credential / secret: a grant here would leak a key, token or hash into the extract ---
            // --- recipe marketplace: private columns (author, organization and user ids, preview source, reporter ids and notes, audit actors) ---
            ["marketplace_recipes"] = "marketplace: author_user_id / author_org_id are private; not in the BI extract scope",
            ["marketplace_recipe_versions"] = "marketplace: preview_source_json, check_json and submitter ids are private",
            ["marketplace_copies"] = "marketplace: carries the copying organization's id and workflow id",
            ["marketplace_ratings"] = "marketplace: carries the rater's user id",
            ["marketplace_reports"] = "marketplace: carries the reporter's user id and a free-text note",
            ["marketplace_audit"] = "marketplace: carries actor user ids and moderation detail",
            ["platform_secrets"] = "credential store: holds the provider/API secrets",
            ["cloud_storage_connections"] = "credential store: cloud storage access keys",
            ["cloud_storage_oauth_states"] = "short-lived cloud storage OAuth state carrying a provider credential",
            ["social_accounts"] = "credential store: social platform access and refresh tokens",
            ["social_oauth_states"] = "short-lived social OAuth state carrying a provider credential",
            ["social_posts"] = "customer post text, remote post ids and failure detail; not in the BI extract scope",
            ["social_post_metrics"] = "per-post performance readings; not in the BI extract scope",
            ["social_publish_ledger"] = "publish-slot counters for caps; not in the BI extract scope",
            ["github_account_links"] = "holds a GitHub access token",
            ["github_oauth_states"] = "short-lived GitHub OAuth state carrying a provider credential",
            ["github_app_installations"] = "holds GitHub App installation credentials",
            ["github_webhook_deliveries"] = "raw webhook payloads, may carry a token",
            ["data_protection_keys"] = "ASP.NET Data Protection key ring",
            ["api_keys"] = "API key secrets hash and scopes; not in the BI model",
            ["api_key_spend"] = "per-key spend rows",
            ["api_key_audit_events"] = "key audit trail",
            ["api_key_rate_counters"] = "rate counters",
            ["api_idempotency_records"] = "idempotency replays may hold response bodies",
            ["api_run_requests"] = "queued run requests",
            ["user_tokens"] = "session and refresh tokens",
            ["oauth_clients"] = "OAuth client secrets",
            ["oauth_authorization_codes"] = "short-lived OAuth authorization codes",
            ["oauth_refresh_tokens"] = "OAuth refresh tokens",

            // --- engine-owned, not in the BI extract scope ---
            ["workflow_definitions"] = "engine-owned workflow definition; not in the BI extract scope",
            ["workflow_steps"] = "engine-owned step definition; not in the BI extract scope",
            ["workflow_step_cache_entries"] = "engine-owned step cache; not in the BI extract scope",
            ["review_scores"] = "engine-owned review output; not in the BI extract scope",
            ["decision_observations"] = "engine-owned decision trace; not in the BI extract scope",
            ["external_generation_jobs"] = "engine-owned external job; not in the BI extract scope",
            ["runner_sessions"] = "engine-owned runner session; not in the BI extract scope",
            ["composition_revisions"] = "engine-owned composition revision; not in the BI extract scope",
            ["object_effect_requests"] = "internal object-search request and timeline-intent bookkeeping; holds user-typed prompts, no reporting value",
            ["step_change_requests"] = "API-owned change request; same scope as composition_revisions, not in the BI extract",
            ["composition_workspaces"] = "engine-owned composition workspace; not in the BI extract scope",
            ["composition_workspace_versions"] = "engine-owned composition version; not in the BI extract scope",
            ["media_derivatives"] = "engine-owned media output; not in the BI extract scope",
            ["edit_timelines"] = "engine-owned edit timeline; not in the BI extract scope",
            ["edit_timeline_ops"] = "engine-owned edit timeline operation; not in the BI extract scope",
            ["edit_review_links"] = "API-owned guest review link (holds a capability token hash); never in the BI extract",
            ["edit_review_decisions"] = "API-owned guest approval record (guest names and notes); not in the BI extract scope",
            ["edit_timeline_comments"] = "engine-owned edit timeline comment; not in the BI extract scope",
            ["edit_timeline_snapshots"] = "engine-owned edit timeline snapshot; not in the BI extract scope",
            ["edit_image_documents"] = "engine-owned image edit document; not in the BI extract scope",
            ["edit_image_layers"] = "engine-owned image edit layer; not in the BI extract scope",
            ["edit_image_exports"] = "engine-owned image edit export; not in the BI extract scope",
            ["edit_image_graphic_requests"] = "engine-owned image edit request; not in the BI extract scope",
            ["editing_styles"] = "engine-owned editing style; not in the BI extract scope",
            ["editor_presets"] = "engine-owned editor preset; not in the BI extract scope",
            ["user_onboarding_states"] = "API-owned per-user onboarding state; not in the BI extract scope",
            ["editor_user_preferences"] = "engine-owned editor preference; not in the BI extract scope",
            ["post_texts"] = "API-owned saved post text of a project video; not in the BI extract scope",
            ["project_pronunciation_entries"] = "API-owned project glossary; not in the BI extract scope",
            ["project_voices"] = "engine-owned project voice; not in the BI extract scope",
            // Avatars (docs/design/avatars.md): a real person's likeness, consent record and audit trail; personal and
            // possibly special-category data, never in the BI extract.
            ["project_avatars"] = "API-owned avatar and consent record of a real person; personal data, not in the BI extract scope",
            ["project_avatar_references"] = "API-owned links from an avatar to its reference media; not in the BI extract scope",
            ["project_avatar_vendor_assets"] = "API-owned vendor-side ids of avatar media; not in the BI extract scope",
            ["project_avatar_events"] = "API-owned avatar audit trail (accountability record); not in the BI extract scope",
            // Workspace avatar library (docs/design/avatar-objects.md): the same likeness and consent record, held per organization.
            ["avatar_library_items"] = "API-owned workspace avatar library item and consent record of a real person; personal data, not in the BI extract scope",
            ["avatar_library_references"] = "API-owned facts and storage keys of a library item's reference media; not in the BI extract scope",
            ["avatar_build_jobs"] = "API-owned avatar build jobs (a member's instruction and object description in their own words); not in the BI extract scope",
            ["project_files"] = "engine-owned project file; not in the BI extract scope",
            ["project_archive_unpacks"] = "API-owned zip unpack request log; file names of customer uploads, not in the BI extract scope",
            ["project_local_folder_links"] = "engine-owned project link; not in the BI extract scope",
            ["project_cloud_links"] = "engine-owned project link; not in the BI extract scope",
            ["project_github_links"] = "engine-owned project link; not in the BI extract scope",
            // Project versioning (contract section 2): history is reached only through an authorized
            // project and is not in the BI extract scope.
            ["project_versions"] = "project history; not in the BI extract scope",
            ["project_version_items"] = "project history; not in the BI extract scope",
            ["project_history_blobs"] = "project history; not in the BI extract scope",
            ["project_history_documents"] = "project history; not in the BI extract scope",

            // --- other API tables the BI model does not need ---
            ["csam_scan_audits"] = "CSAM evidence audit; admin-only and not in the BI model",
            ["assistant_allowance_holds"] = "assistant subsystem; not in the BI model",
            ["assistant_run_watches"] = "transient assistant wake requests; not in the BI model",
            ["assistant_messages"] = "assistant subsystem; not in the BI model",
            ["assistant_threads"] = "assistant subsystem; not in the BI model",
            ["mcp_tool_calls"] = "MCP tool-call audit; not in the BI model",
            ["output_moderation_verdicts"] = "API-owned stored publish verdict per output; not in the BI model",
            ["content_moderation_decisions"] = "security audit: moderation decisions and category scores; not in the BI model",
            ["marketing_consents"] = "marketing consent record; not in the BI model",
            ["marketing_consent_events"] = "marketing consent event; not in the BI model",
            ["marketing_consent_exports"] = "marketing consent export; not in the BI model",
            ["marketing_suppressions"] = "marketing suppression list; not in the BI model",
            ["marketing_brevo_erasures"] = "protected addresses awaiting deletion at Brevo; not in the BI model",
            ["marketing_contact_sync_state"] = "Brevo contact sync worker state; not in the BI model",
            ["marketing_campaigns"] = "marketing campaign drafts and results; not in the BI model",
            ["mail_rate_counters"] = "mail rate limiting; not in the BI model",
            ["first_run_reminders"] = "first-run reminder claim per user; not in the BI model",
            ["legal_documents"] = "legal document text; not in the BI model",
            ["legal_acceptances"] = "legal acceptance audit; not in the BI model",
            ["billing_admin_actions"] = "billing admin audit; not in the BI model",
            ["admin_audit_events"] = "platform-admin user-change security audit (IPs, user agents); admin-only, not in the BI model",
            ["billing_customers"] = "billing customer record; not in the BI model",
            ["billing_pending_upgrade_grants"] = "billing pending grant; not in the BI model",
            ["billing_reconcile_watches"] = "billing reconciliation; not in the BI model",
            ["billing_webhook_events"] = "raw billing webhook payloads; not in the BI model",
            ["promotions"] = "promotion catalogue; not in the BI model",
            ["promotion_checkouts"] = "promotion checkout; not in the BI model",
            ["promotion_redemptions"] = "promotion redemption; not in the BI model",
            ["admin_credit_grants"] = "admin credit-grant audit; not in the BI model",
            // A gift is told from revenue on subscriptions.provider IS NULL, which is granted; the
            // audit row behind it carries the reason and who gave it, like admin_credit_grants.
            ["admin_subscription_grants"] = "admin plan-grant audit; not in the BI model",
            ["trial_claims"] = "trial claim; not in the BI model",
            ["alpha_funnel_claims"] = "alpha funnel claim; not in the BI model",
            ["user_notifications"] = "notification inbox; not in the BI model",
            ["user_notification_preferences"] = "notification preference; not in the BI model",
            ["feedback_reports"] = "feedback inbox; not in the BI model",
            ["run_rate_events"] = "rate-limit event; not in the BI model",
            ["webhook_endpoints"] = "customer webhook URLs and encrypted signing secrets; not in the BI model",
            ["webhook_deliveries"] = "webhook delivery queue and log; not in the BI model",
            ["storage_retention_marks"] = "retention bookkeeping; not in the BI model",
            ["design_systems"] = "design system library; not in the BI model",
            ["design_system_sources"] = "design system source; not in the BI model",
            ["design_system_versions"] = "design system version; not in the BI model",
            ["design_system_libraries"] = "design system library; not in the BI model",
            ["design_system_library_assets"] = "library asset metadata; not in the BI model",
            ["design_system_change_proposals"] = "design system proposal; not in the BI model",
            ["platform_settings"] = "platform configuration; not in the BI model",
            ["organization_invites"] = "organization invite; not in the BI model",
            ["organization_agent_provider_overrides"] = "provider override; not in the BI model",
            ["model_capabilities"] = "model capability catalogue; not in the BI model",
            ["model_capability_prices"] = "model capability price; not in the BI model",
            ["sfx_library_items"] = "built-in sound library catalogue (platform-owned, no customer data); not in the BI model",
            ["user_video_spend_limits"] = "a person's own daily video limit setting; not in the BI model",
            ["runner_pairing_requests"] = "runner pairing; not in the BI model",
            ["media_bins"] = "media library; not in the BI model",
            ["media_bin_items"] = "media library item; not in the BI model",
            ["media_annotations"] = "media annotation; not in the BI model",
            ["media_analysis_requests"] = "on-demand analysis request state; not in the BI model",
            ["media_analysis_results"] = "digest-cached analysis output (bulk JSON); not in the BI model",
        };

    /// <summary>
    /// The twenty-nine granted tables, table-wide or column-by-column: the twenty-four of the BI brief and
    /// its follow-ups, plus csp_violation_counts (security-headers) and the four new-user funnel tables (docs/design/new-user-funnel.md section 5:
    /// <c>marketing_links</c>, <c>marketing_link_daily_stats</c>, <c>visitor_attribution</c>,
    /// <c>user_onboarding_profiles</c>), which the acquisition funnel reads.
    /// </summary>
    private static readonly string[] TheGrantedTables =
    {
        "agent_definitions",
        "application_users",
        "billing_payments",
        "billing_refunds",
        "credit_accounts",
        "credit_grants",
        "credit_ledger_entries",
        "csp_violation_counts",
        "inference_models",
        "inference_providers",
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
        "runner_jobs",
        "subscriptions",
        "usage_events",
        "user_attribution",
        "user_onboarding_profiles",
        "visitor_attribution",
        "workflow_executions",
        "workflow_step_results"
    };

    /// <summary>Prefixes of the never-grant tables: a grant on any of these is a defect.</summary>
    private static readonly string[] NeverGrantPrefixes =
    {
        "platform_secrets",
        "cloud_storage_",
        "social_accounts",
        "social_oauth_states",
        "github_",
        "data_protection_keys",
        "user_tokens",
        "oauth_"
    };

    [Fact]
    public void Every_public_table_is_either_granted_to_powerbi_ro_or_explicitly_excluded()
    {
        IReadOnlyList<string> all = AllModelTables();
        HashSet<string> granted = GrantedTables();

        List<string> unclassified = all.Where(t => !granted.Contains(t) && !Excluded.ContainsKey(t)).ToList();
        unclassified.Should().BeEmpty(
            "table(s) {0} are new: add each to PowerBiReadOnlyRoleSql/…EngineSql, or to Excluded in this test "
            + "with a reason - an unclassified table is invisible to powerbi_ro and breaks the nightly extract silently.",
            string.Join(", ", unclassified));

        // Excluded must cover all - granted exactly: nothing missing and no stale entry.
        List<string> shouldBeExcluded = all.Where(t => !granted.Contains(t)).ToList();
        List<string> uncovered = shouldBeExcluded.Where(t => !Excluded.ContainsKey(t)).ToList();
        uncovered.Should().BeEmpty("every table that is not granted must be classified in Excluded");

        List<string> stale = Excluded.Keys.Where(t => !shouldBeExcluded.Contains(t)).ToList();
        stale.Should().BeEmpty(
            "Excluded lists {0}, which are either granted or no longer in the model - remove the stale entry",
            string.Join(", ", stale));

        List<string> reasonless = Excluded.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        reasonless.Should().BeEmpty("every Excluded entry needs a one-line reason");
    }

    [Fact]
    public void The_granted_tables_are_exactly_the_set_the_brief_names()
    {
        GrantedTables().OrderBy(t => t, StringComparer.Ordinal)
            .Should().Equal(TheGrantedTables.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// The convergence half: a grant is only narrowed correctly if a table-level <c>REVOKE</c> names the
    /// table first. A grant added without one leaves an older, wider grant in place on a database that
    /// carries it, so the "convergent" block silently converges nothing - the exact failure the
    /// convergence exists to prevent. This turns that from a review finding into a red build.
    /// </summary>
    [Fact]
    public void Every_granted_table_is_named_by_a_table_level_revoke()
    {
        HashSet<string> revoked = RevokedTables(PowerBiReadOnlyRoleSql.Up)
            .Concat(RevokedTables(PowerBiReadOnlyRoleEngineSql.Up))
            .ToHashSet(StringComparer.Ordinal);

        List<string> unrevoked = GrantedTables()
            .Where(t => !revoked.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        unrevoked.Should().BeEmpty(
            "every granted table must be named by a table-level REVOKE in the grant block, or a later narrowing never converges");
    }

    /// <summary>
    /// The tables named by the table-level <c>REVOKE SELECT ON … FROM powerbi_ro</c> statements in
    /// <paramref name="sql"/>. Column-level revokes (<c>REVOKE SELECT (a, b) ON t …</c>) are not
    /// matched, because their <c>ON</c> follows the parenthesis.
    /// </summary>
    private static IEnumerable<string> RevokedTables(string sql)
    {
        foreach (Match statement in Regex.Matches(sql, @"REVOKE SELECT ON\s+([^;]*?)\s+FROM powerbi_ro;"))
        {
            foreach (string name in statement.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string token in name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return token;
                }
            }
        }
    }

    [Fact]
    public void No_credential_column_is_granted()
    {
        // The list is pinned verbatim: it spans both contexts - agent_definitions.system_prompt and
        // agent_definitions.config_json (set verbatim from a caller-supplied request) here;
        // workflow_executions.agent_snapshot_json / error_message / result_json / user_request /
        // style_config_json (a freely written StyleConfig.Notes, which the prompt-injection sanitizer
        // does not redact) and workflow_step_results.error_details / tool_calls_json / reasoning_json
        // / chat_transcript_json / input_json / output_json / output on the engine side; the two
        // inference_providers credential columns; and application_users.signup_ip (PII the BI model has
        // no use for) plus application_users.password_hash.
        PowerBiReadOnlyRoleSql.SensitiveColumnsNotGranted.Should().Equal(
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
            "run_brief_json",
            "config_json");

        IReadOnlyDictionary<string, IReadOnlyList<string>> modelColumns = ModelColumnsByTable();

        // A table granted table-wide reaches EVERY column the model maps to it, so the effective
        // granted columns are the model's columns, not a hand-written list. BOTH registries are
        // visited: the engine registry (runner_jobs table-wide; workflow_executions and
        // workflow_step_results column-by-column) lives in the other context, and a table-wide grant
        // there is exactly the shape that leaked workflow_executions.agent_snapshot_json past the
        // deliberate agent_definitions.system_prompt exclusion. A guard that checked the API registry
        // alone would read as coverage without being it.
        foreach (string table in PowerBiReadOnlyRoleSql.FullSelectTables
                     .Concat(PowerBiReadOnlyRoleEngineSql.FullSelectTables))
        {
            modelColumns.Should().ContainKey(table,
                "the guard must be able to derive the effective columns of the table-wide grant on {0}", table);

            foreach (string column in modelColumns[table])
            {
                PowerBiReadOnlyRoleSql.SensitiveColumnsNotGranted.Should().NotContain(column,
                    "table-wide SELECT on {0} grants the sensitive column {0}.{1}", table, column);
            }
        }

        // A table granted column-by-column reaches only the listed columns; neither registry's list
        // may name a sensitive column.
        foreach (KeyValuePair<string, IReadOnlyList<string>> table in
                 PowerBiReadOnlyRoleSql.ColumnSelectTables.Concat(PowerBiReadOnlyRoleEngineSql.ColumnSelectTables))
        {
            foreach (string column in table.Value)
            {
                PowerBiReadOnlyRoleSql.SensitiveColumnsNotGranted.Should().NotContain(column,
                    "the sensitive column {0}.{1} must not be granted", table.Key, column);
            }
        }

        // The five column-granted lists are pinned to their final shape by EQUALITY against the model,
        // not by count. The loops above prove no sensitive column is named; a count plus those loops is
        // still blind to the complement - add a non-sensitive column to the model and leave it out of
        // the grant, and the count still matches, so the column silently drops out of the extract with a
        // green build (the LOW T-005 raised). Equality against modelColumns minus
        // SensitiveColumnsNotGranted makes both directions red: a column added to the model and not
        // granted, and a granted name that is no longer a column of the model. The nineteen sensitive
        // names do not collide across these five tables, so the subtraction is unambiguous. The third
        // review round moved three of these lists (application_users lost signup_ip; workflow_step_results
        // lost error_details, tool_calls_json and reasoning_json) and the fourth moved the prose columns
        // (workflow_executions lost result_json and user_request; workflow_step_results lost
        // chat_transcript_json, input_json, output_json and output); the fifth moved the last two
        // caller-authored free-text columns (agent_definitions lost config_json; workflow_executions
        // lost style_config_json), which is exactly the drift this pins. Walked across BOTH registries,
        // the same two the effective-columns loops above walk.
        foreach (KeyValuePair<string, IReadOnlyList<string>> granted in
                 PowerBiReadOnlyRoleSql.ColumnSelectTables.Concat(PowerBiReadOnlyRoleEngineSql.ColumnSelectTables))
        {
            string table = granted.Key;

            modelColumns.Should().ContainKey(table,
                "the guard must be able to derive the model columns of the column-granted table {0}", table);

            List<string> expected = modelColumns[table]
                .Where(c => !PowerBiReadOnlyRoleSql.SensitiveColumnsNotGranted.Contains(c))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            granted.Value.OrderBy(c => c, StringComparer.Ordinal).Should().Equal(expected,
                "the grant on {0} must be exactly the model's mapped columns minus SensitiveColumnsNotGranted: a "
                + "column added to the model and not granted here is invisible to powerbi_ro and the extract "
                + "silently loses it, and a name granted here that the model no longer maps is stale",
                table);
        }
    }

    [Fact]
    public void No_never_grant_secret_table_is_granted()
    {
        HashSet<string> granted = GrantedTables();
        List<string> offenders = granted
            .Where(t => NeverGrantPrefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)))
            .ToList();
        offenders.Should().BeEmpty("a token/key table ({0}) must never be granted to powerbi_ro", string.Join(", ", offenders));
    }

    [Fact]
    public void The_granted_and_excluded_lists_do_not_overlap()
    {
        HashSet<string> granted = GrantedTables();
        granted.Intersect(Excluded.Keys).Should().BeEmpty("a table cannot be both granted and excluded");
    }

    /// <summary>The distinct table names of the union of both EF models (about a hundred).</summary>
    private static IReadOnlyList<string> AllModelTables()
    {
        using InferenceApiDbContext api = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);
        using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);

        return TableNames(api).Concat(TableNames(engine)).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> TableNames(DbContext db) =>
        db.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .OfType<string>();

    /// <summary>
    /// Table name to the set of column names the union of both EF models maps to it. This is what a
    /// table-wide <c>SELECT</c> effectively grants: deriving it from the model (rather than a
    /// hand-written list) is what lets <see cref="No_credential_column_is_granted"/> catch a sensitive
    /// column the moment one is added to a table granted table-wide.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ModelColumnsByTable()
    {
        using InferenceApiDbContext api = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);
        using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql("Host=localhost;Database=model-only").Options);

        Dictionary<string, SortedSet<string>> byTable = new(StringComparer.Ordinal);
        foreach (DbContext db in new DbContext[] { api, engine })
        {
            foreach (IEntityType entity in db.GetService<IDesignTimeModel>().Model.GetEntityTypes())
            {
                string? table = entity.GetTableName();
                if (table is null)
                {
                    continue;
                }

                StoreObjectIdentifier storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
                foreach (IProperty property in entity.GetProperties())
                {
                    string? column = property.GetColumnName(storeObject);
                    if (column is null)
                    {
                        continue;
                    }

                    if (!byTable.TryGetValue(table, out SortedSet<string>? columns))
                    {
                        columns = new SortedSet<string>(StringComparer.Ordinal);
                        byTable[table] = columns;
                    }

                    columns.Add(column);
                }
            }
        }

        return byTable.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>The union of the three grant sources: API table-wide, API column-by-column, engine-owned.</summary>
    private static HashSet<string> GrantedTables() =>
        new HashSet<string>(
            PowerBiReadOnlyRoleSql.FullSelectTables
                .Concat(PowerBiReadOnlyRoleSql.ColumnSelectTables.Keys)
                .Concat(PowerBiReadOnlyRoleEngineSql.GrantedTables),
            StringComparer.Ordinal);
}
