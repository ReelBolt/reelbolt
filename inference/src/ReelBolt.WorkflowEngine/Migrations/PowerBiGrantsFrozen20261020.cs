namespace ReelBolt.WorkflowEngine.Migrations;

/// <summary>
/// The grant block exactly as it stood when
/// <see cref="AddPowerBiReadOnlyRoleEngineGrants"/> was created (commit <c>d116462c2</c>), frozen
/// here so the migration never references a column another migration adds later. A fresh database
/// with the cluster-wide <c>powerbi_ro</c> role present runs this migration before
/// <c>workflow_executions.attention_json</c>, <c>first_started_at</c> and <c>api_key_id</c> exist, so
/// a reference to the live <c>PowerBiReadOnlyRoleEngineSql.Up</c> aborted with 42703. The head ACL
/// is converged by <see cref="ConvergePowerBiReadOnlyRoleEngineGrants"/>; the live constant
/// describes the operator script's current state.
/// </summary>
internal static class PowerBiGrantsFrozen20261020
{
    /// <summary>
    /// <c>PowerBiReadOnlyRoleEngineSql.Up</c> at <c>d116462c2</c>, verbatim. Do not refresh it: a
    /// column added after 2026-10-20 must not appear here.
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
    REVOKE SELECT (agent_snapshot_json, error_message, result_json, user_request)
      ON workflow_executions FROM powerbi_ro;
    REVOKE SELECT (error_details, tool_calls_json, reasoning_json, input_json, output, output_json,
      chat_transcript_json) ON workflow_step_results FROM powerbi_ro;
    REVOKE SELECT (result_json) ON runner_jobs FROM powerbi_ro;
    REVOKE SELECT ON workflow_executions, workflow_step_results, runner_jobs FROM powerbi_ro;
    GRANT SELECT (admitted_run_rate_bucket, auto_resume_count, completed_at, correlation_id,
      current_step_id, design_system_pin_json, heartbeat_at, id, initiated_by_user_id,
      initiator, iteration_count, lease_expires_at, lease_owner, organization_id,
      output_format_override, output_resolution, pending_resume, project_id, queued_at,
      resume_count, snapshot_definition_id, started_at, status,
      style_config_json, workflow_definition_id)
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
}
