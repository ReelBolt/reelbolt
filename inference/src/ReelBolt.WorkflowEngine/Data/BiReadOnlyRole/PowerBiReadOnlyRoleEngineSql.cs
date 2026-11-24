namespace ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;

/// <summary>
/// The raw SQL of the engine grant block: the three WorkflowEngine-owned
/// tables the <c>powerbi_ro</c> BI-extract role reads. The role itself and its read-only hardening are
/// created by the API block of the operator script (<c>PowerBiReadOnlyRoleSql</c>,
/// <c>infra/powerbi/postgres-readonly-role.sql</c>), which runs first; these three
/// tables are granted here because they do not exist yet when the API block runs on a fresh database.
/// Kept as named, public constants so a test can read exactly what the script runs (precedent:
/// <c>PowerBiReadOnlyRoleSql</c>).
/// <para>
/// The grant is guarded on the role existing, so the engine may run against a database where the API
/// block has not created <c>powerbi_ro</c> yet: a missing role is a no-op, not an error.
/// </para>
/// <para>
/// <c>workflow_executions</c> and <c>workflow_step_results</c> are granted column-by-column, not
/// table-wide. <c>workflow_executions.agent_snapshot_json</c> is the run's frozen agent snapshot and
/// serializes each <c>AgentSnapshotEntry.SystemPrompt</c> verbatim, so a table-wide grant would ship the
/// proprietary system prompts the API block deliberately withholds on
/// <c>agent_definitions.system_prompt</c> to the extract's CSV and object storage.
/// <c>workflow_executions.error_message</c> and <c>workflow_step_results.error_details</c> are written
/// from <c>ex.Message</c>, and several provider clients embed a bounded vendor error body in that
/// message; the bounding (<c>ImagePayload.Bounded</c>) is documented as key-scrubbed but performs no
/// redaction, so a vendor that echoes the request could land a provider key in the extract. Neither
/// column can be proven secret-free and both are withheld.
/// <c>workflow_step_results.tool_calls_json</c> serializes each tool call's name, arguments and result,
/// and a tool's arguments and result are arbitrary - <c>ReadProjectFile</c> returns file contents and a
/// provider-test tool takes provider fields - so nothing about that column is provable either.
/// <c>workflow_step_results.reasoning_json</c> is the model's private reasoning, and the system prompt is
/// shown to the model as chat instructions, so the reasoning can restate an excluded column verbatim;
/// it has no BI value. The same argument withholds the prose columns
/// <c>workflow_executions.result_json</c> and <c>user_request</c> and
/// <c>workflow_step_results.input_json</c>, <c>output</c>, <c>output_json</c> and
/// <c>chat_transcript_json</c>: <c>input_json</c> composes the caller's request, and a caller can paste
/// a secret into <c>user_request</c>; the model then sees it and can restate it in the prose output.
/// That cannot be proven secret-free, so none of them is granted. <c>style_config_json</c> carries a
/// <c>StyleConfig.Notes</c> the user writes freely (the sanitizer that touches it is a
/// prompt-injection defense, not a redactor), so it is withheld on the same caller-authored ground as
/// <c>user_request</c>. <c>runner_jobs.result_json</c> is
/// withheld for the same reason: the runner gateway writes the runner's whole finish body there
/// (<c>sandbox/cmd/runner-gateway/queue.go</c>, "the whole fail body is kept in result_json"), which
/// is arbitrary runner-reported content and cannot be proven secret-free either. <c>runner_jobs</c> is
/// therefore granted column-by-column too, keeping the job metadata (status, attempts, leases,
/// timestamps, <c>error_class</c>) and nothing else.
/// </para>
/// </summary>
public static class PowerBiReadOnlyRoleEngineSql
{
    /// <summary>
    /// The WorkflowEngine-owned tables granted table-wide <c>SELECT</c>, sorted: empty, because every
    /// engine table carries a column the extract must not read. The registry below is a bare column
    /// name, so a table-wide grant here would reach a sensitive column of that table; the guard test
    /// fails on exactly that.
    /// </summary>
    public static readonly IReadOnlyList<string> FullSelectTables = Array.Empty<string>();

    /// <summary>
    /// The WorkflowEngine-owned tables granted column-by-column, each mapped to exactly the columns
    /// granted, sorted. The excluded column of each is a credential, a run's frozen agent snapshot, a
    /// raw vendor error body, or text whose content is arbitrary (a serialized tool call, the model's
    /// own reasoning, the caller's request, the model's prose output or the runner's finish body) and
    /// therefore cannot be proven secret-free; none may reach the extract.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ColumnSelectTables =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["workflow_executions"] = new[]
            {
                "admitted_run_rate_bucket",
                "api_key_id",
                "attention_json",
                "auto_resume_count",
                "completed_at",
                "correlation_id",
                "current_step_id",
                "design_system_pin_json",
                "first_started_at",
                "heartbeat_at",
                "id",
                "initiated_by_user_id",
                "initiator",
                "iteration_count",
                "lease_expires_at",
                "lease_owner",
                "organization_id",
                "output_format_override",
                "output_resolution",
                "pending_resume",
                "project_id",
                "queued_at",
                "resume_count",
                "snapshot_definition_id",
                "started_at",
                "status",
                "workflow_definition_id"
            },
            ["workflow_step_results"] = new[]
            {
                "artifact_storage_key",
                "cached_tokens_saved",
                "completed_at",
                "compute_target",
                "credits_charged_millis",
                "credits_unsettled_millis",
                "duration_ms",
                "executed_at",
                "failure_class",
                "failure_code",
                "from_cache",
                "from_runner",
                "id",
                "input_tokens",
                "is_pinned",
                "iteration_number",
                "output_storage_key",
                "output_tokens",
                "primary_model_name",
                "primary_provider_id",
                "reused_from_step_result_id",
                "runner_id",
                "runner_job_id",
                "status",
                "superseded_at",
                "timeline_storage_key",
                "tokens_used",
                "workflow_execution_id",
                "workflow_step_id"
            },
            ["runner_jobs"] = new[]
            {
                "attempt",
                "completed_at",
                "created_at",
                "error_class",
                "execution_id",
                "gateway_instance",
                "id",
                "idempotency_key",
                "lease_epoch",
                "lease_expires_at",
                "lease_owner_runner_id",
                "max_attempts",
                "organization_id",
                "priority",
                "required_caps_json",
                "spec_json",
                "status",
                "step_id",
                "target",
                "type",
                "updated_at"
            }
        };

    /// <summary>
    /// The WorkflowEngine-owned tables granted <c>SELECT</c> in either form, sorted: the union of
    /// <see cref="FullSelectTables"/> and <see cref="ColumnSelectTables"/>, so a reader that only needs
    /// "which tables can the role read at all" (the guard test's classification) does not have to know
    /// the form of the grant.
    /// </summary>
    public static readonly IReadOnlyList<string> GrantedTables =
        FullSelectTables.Concat(ColumnSelectTables.Keys).OrderBy(t => t, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Up: the convergent revoke-then-grant block, guarded on <c>powerbi_ro</c> existing so a fresh
    /// database (role absent) is a no-op. It names the withheld columns in an explicit column-level
    /// <c>REVOKE</c> (belt-and-braces: the table-level <c>REVOKE</c> below is the mechanism, and on
    /// PostgreSQL 16 already clears column grants), clears any table-level <c>SELECT</c> an earlier
    /// revision of this grant left on the three tables, then grants column-by-column SELECT on
    /// <c>workflow_executions</c> (every column but <c>agent_snapshot_json</c>, <c>error_message</c>,
    /// <c>result_json</c>, <c>user_request</c> and <c>style_config_json</c>), <c>workflow_step_results</c> (every column but
    /// <c>error_details</c>, <c>tool_calls_json</c>, <c>reasoning_json</c>, <c>input_json</c>,
    /// <c>output</c>, <c>output_json</c> and <c>chat_transcript_json</c>) and <c>runner_jobs</c>
    /// (every column but <c>result_json</c>). It needs no CREATEROLE, so the engine grant migration runs it as
    /// well as the operator script. The leading REVOKE is what makes the statement set idempotent and
    /// convergent: a database that applied an earlier table-wide revision keeps a stale table-level
    /// entry that would otherwise shadow the narrowed column grants.
    /// </summary>
    public const string Up =
        @"DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    -- belt-and-braces: name every column any revision of this grant ever granted and the current one
    -- withholds, so the intent (clear anything a revision granted) is stated explicitly. This is NOT
    -- the mechanism: a table-level REVOKE SELECT ON t FROM r already clears column-level grants on
    -- PostgreSQL 16 (verified empirically - after GRANT SELECT (a) a table-level REVOKE leaves attacl
    -- empty and has_column_privilege false). The table-level REVOKE below does the work; these lines
    -- are idempotent no-ops kept for intent, not load-bearing.
    REVOKE SELECT (agent_snapshot_json, error_message, result_json, user_request, style_config_json)
      ON workflow_executions FROM powerbi_ro;
    REVOKE SELECT (error_details, tool_calls_json, reasoning_json, input_json, output, output_json,
      chat_transcript_json) ON workflow_step_results FROM powerbi_ro;
    REVOKE SELECT (result_json) ON runner_jobs FROM powerbi_ro;
    REVOKE SELECT ON workflow_executions, workflow_step_results, runner_jobs FROM powerbi_ro;
    GRANT SELECT (admitted_run_rate_bucket, api_key_id, attention_json, auto_resume_count, completed_at, correlation_id,
      current_step_id, design_system_pin_json, first_started_at, heartbeat_at, id, initiated_by_user_id,
      initiator, iteration_count, lease_expires_at, lease_owner, organization_id,
      output_format_override, output_resolution, pending_resume, project_id, queued_at,
      resume_count, snapshot_definition_id, started_at, status, workflow_definition_id)
      ON workflow_executions TO powerbi_ro;
    GRANT SELECT (artifact_storage_key, cached_tokens_saved, completed_at,
      compute_target, credits_charged_millis, credits_unsettled_millis, duration_ms, executed_at,
      failure_class, failure_code, from_cache, from_runner, id, input_tokens,
      is_pinned, iteration_number, output_storage_key, output_tokens,
      primary_model_name, primary_provider_id, reused_from_step_result_id,
      runner_id, runner_job_id, status, superseded_at, timeline_storage_key, tokens_used,
      workflow_execution_id, workflow_step_id)
      ON workflow_step_results TO powerbi_ro;
    GRANT SELECT (attempt, completed_at, created_at, error_class, execution_id, gateway_instance, id,
      idempotency_key, lease_epoch, lease_expires_at, lease_owner_runner_id, max_attempts,
      organization_id, priority, required_caps_json, spec_json, status, step_id, target, type,
      updated_at) ON runner_jobs TO powerbi_ro;
  END IF;
END $$;";

    /// <summary>
    /// ApiKeyIdColumnGrantSql: grants the BI role the <c>workflow_executions.api_key_id</c> column so
    /// the model can attribute API-initiated runs to the key that started them. Guarded on the role
    /// existing. A migration that adds the column calls this from its Up (the registry block is not
    /// re-run by later migrations).
    /// </summary>
    public const string ApiKeyIdColumnGrantSql =
        @"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='powerbi_ro') THEN GRANT SELECT (api_key_id) ON workflow_executions TO powerbi_ro; END IF; END $$;";

    /// <summary>
    /// Down: revoke both grant forms unconditionally - the table-level entry and the column-level grants
    /// for the two column-granted tables - so the role returns to a known state whichever revision of
    /// <see cref="Up"/> ran first. Guarded on <c>powerbi_ro</c> existing, since a REVOKE from a missing
    /// role is an error, not a no-op.
    /// </summary>
    public const string Down =
        @"DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN
    REVOKE SELECT ON workflow_executions, workflow_step_results, runner_jobs FROM powerbi_ro;
    REVOKE SELECT (admitted_run_rate_bucket, api_key_id, auto_resume_count, completed_at, correlation_id,
      current_step_id, design_system_pin_json, first_started_at, heartbeat_at, id, initiated_by_user_id,
      initiator, iteration_count, lease_expires_at, lease_owner, organization_id,
      output_format_override, output_resolution, pending_resume, project_id, queued_at,
      resume_count, snapshot_definition_id, started_at, status, workflow_definition_id)
      ON workflow_executions FROM powerbi_ro;
    REVOKE SELECT (artifact_storage_key, cached_tokens_saved, completed_at,
      compute_target, credits_charged_millis, credits_unsettled_millis, duration_ms, executed_at,
      failure_class, failure_code, from_cache, from_runner, id, input_tokens,
      is_pinned, iteration_number, output_storage_key, output_tokens,
      primary_model_name, primary_provider_id, reused_from_step_result_id,
      runner_id, runner_job_id, status, superseded_at, timeline_storage_key, tokens_used,
      workflow_execution_id, workflow_step_id)
      ON workflow_step_results FROM powerbi_ro;
    REVOKE SELECT (attempt, completed_at, created_at, error_class, execution_id, gateway_instance, id,
      idempotency_key, lease_epoch, lease_expires_at, lease_owner_runner_id, max_attempts,
      organization_id, priority, required_caps_json, spec_json, status, step_id, target, type,
      updated_at) ON runner_jobs FROM powerbi_ro;
  END IF;
END $$;";
}
