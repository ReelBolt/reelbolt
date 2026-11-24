-- Throwaway schema for the D13 local smoke test (sandbox/scripts/worker-smoke.sh).
--
-- It is the SAME schema the gateway's own queue integration tests create, and
-- TestSmokeSchemaMatchesTheGateways in cmd/reelbolt-worker fails when the two
-- drift, so this file cannot quietly stop describing the real tables. Both are
-- transcribed from the migrations:
--
--   ReelBolt.Inference.Api/Migrations/*_AddRunnerDevices.cs   (runner_devices)
--   ReelBolt.WorkflowEngine/Migrations/*_AddRunnerJobsAndSessions.cs
--                                                  (runner_jobs, runner_sessions)
--
-- The foreign keys to application_users/organizations are omitted: this database
-- has no such tables. Every column, type, constraint and index the gateway's SQL
-- touches is exactly as the migrations create them.
--
-- IT IS FOR A THROWAWAY DATABASE ONLY. Production schema is owned by EF
-- migrations and the gateway never migrates anything.

CREATE TABLE IF NOT EXISTS runner_devices (
	id uuid PRIMARY KEY,
	organization_id uuid NULL,
	kind character varying(50) NOT NULL,
	name character varying(200) NOT NULL,
	public_key text NULL,
	public_key_fingerprint character varying(128) NULL,
	capabilities_json jsonb NULL,
	os text NULL,
	arch text NULL,
	runner_version text NULL,
	last_seen_at timestamp with time zone NULL,
	revoked_at timestamp with time zone NULL,
	revoked_by_user_id uuid NULL,
	created_by_user_id uuid NULL,
	paired_by_user_id uuid NULL,
	created_at timestamp with time zone NOT NULL DEFAULT now(),
	CONSTRAINT ck_runner_devices_org_or_pool CHECK (kind = 'CloudPool' OR organization_id IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_devices_public_key_fingerprint" ON runner_devices (public_key_fingerprint);

CREATE TABLE IF NOT EXISTS runner_jobs (
	id uuid PRIMARY KEY,
	idempotency_key character varying(200) NOT NULL,
	organization_id uuid NOT NULL,
	execution_id uuid NOT NULL,
	step_id uuid NULL,
	type character varying(50) NOT NULL,
	target character varying(50) NOT NULL,
	required_caps_json jsonb NULL,
	spec_json jsonb NULL,
	status character varying(50) NOT NULL,
	priority integer NOT NULL,
	attempt integer NOT NULL,
	max_attempts integer NOT NULL,
	lease_owner_runner_id uuid NULL,
	lease_epoch bigint NOT NULL,
	lease_expires_at timestamp with time zone NULL,
	gateway_instance character varying(200) NULL,
	result_json jsonb NULL,
	error_class character varying(50) NULL,
	created_at timestamp with time zone NOT NULL,
	updated_at timestamp with time zone NOT NULL,
	completed_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_jobs_idempotency_key" ON runner_jobs (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_runner_jobs_execution_id ON runner_jobs (execution_id);
CREATE INDEX IF NOT EXISTS "IX_runner_jobs_lease_expires_at" ON runner_jobs (lease_expires_at) WHERE status IN ('Assigned', 'Running');
CREATE INDEX IF NOT EXISTS "IX_runner_jobs_status_target_organization_id_created_at" ON runner_jobs (status, target, organization_id, created_at);

CREATE TABLE IF NOT EXISTS runner_sessions (
	id uuid PRIMARY KEY,
	job_id uuid NOT NULL,
	execution_id uuid NOT NULL,
	runner_id uuid NOT NULL,
	gateway_instance character varying(200) NULL,
	opened_at timestamp with time zone NOT NULL,
	last_op_at timestamp with time zone NULL,
	closed_at timestamp with time zone NULL,
	CONSTRAINT fk_runner_sessions_runner_jobs_job_id FOREIGN KEY (job_id) REFERENCES runner_jobs (id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_runner_sessions_job_id ON runner_sessions (job_id);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_sessions_execution_id" ON runner_sessions (execution_id) WHERE closed_at IS NULL;

-- The reaper's second duty reads the execution's status. The engine owns this
-- table; only these two columns are ever read, and the status column is text
-- here exactly as the engine's enum-to-string conversion stores it.
CREATE TABLE IF NOT EXISTS workflow_executions (
	id uuid PRIMARY KEY,
	status text NOT NULL
);

-- F11's platform_settings. A missing row means ON; the smoke test leaves it
-- empty so compute is not paused.
CREATE TABLE IF NOT EXISTS platform_settings (
	key text NOT NULL PRIMARY KEY,
	paused boolean NOT NULL DEFAULT false,
	reason text NULL,
	pool_slot_cap integer NULL,
	updated_at timestamp with time zone NOT NULL DEFAULT now(),
	updated_by text NULL
);
