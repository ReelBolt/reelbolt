-- ReelBolt workflow engine Postgres roles (least privilege).
--
-- The workflow engine runs on the render host, next to untrusted render workloads, so the login it
-- uses there must be able to do only what the engine's code does: read and write rows. In particular
-- it must NOT own a table or be able to create anything. A role that owns a table can attach a
-- trigger to it, and the trigger's function then runs with the privileges of whoever writes to the
-- table next. The Inference API writes to the engine's tables all the time and logs in as a superuser
-- or owner, so table ownership on the render host's login would be a path to superuser.
--
-- Three roles, created and converged by this script:
--
--   reelbolt_engine_owner     NOLOGIN. Owns the engine's tables and "__EFMigrationsHistory_Workflow".
--                             Holds USAGE and CREATE on schema public, REFERENCES on projects(id) and
--                             agent_definitions(id) (the engine tables' foreign keys) and SELECT on
--                             projects(id, organization_id) (read by the engine's own migrations).
--                             Nobody logs in as it.
--   reelbolt_engine_migrator  Runs the engine's migrations (the engine binary with --migrate-only).
--                             A member of the owner role with "role" preset to it, so every session
--                             acts as the owner and whatever a migration creates is owned by the
--                             owner role, never by the migrator. Its credential lives on the CONTROL
--                             host only. It can log in only once a password is given
--                             (engine_migrator_password); without one it is created NOLOGIN and the
--                             migrations are run by a superuser, as before.
--   reelbolt_engine           The engine's runtime login, the one the render host holds. Rows only:
--                             it owns nothing, has no CREATE on any schema, no TEMPORARY on the
--                             database, no TRIGGER, REFERENCES or TRUNCATE on any table and is a
--                             member of no role. The exact grants are the 'grants' list below.
--
-- Run it against the app database as a superuser:
--
--   psql "$DATABASE_URL" -f infra/postgres/engine-role.sql
--
-- Order on every release (the script is cheap and re-runnable, so run it at both points):
--   1. the Inference API migrates;
--   2. this script (the owner role gets what the engine's migrations need);
--   3. the engine's migrations: the engine binary with --migrate-only and the migrator's connection
--      string (ConnectionStrings__MigrationConnection), or a superuser run;
--   4. this script again, then gateway-role.sql (tables a migration just created get their grants;
--      a table a superuser created is handed to the owner role);
--   5. start the engine with Database__MigrateAtStartup=false. It refuses to start while a migration
--      is pending, so a skipped step 3 is a clear startup error and not a half-working engine.
--
-- psql variables (all optional):
--
--   -v engine_role=NAME                    runtime role, default reelbolt_engine
--   -v engine_owner_role=NAME              owner role, default reelbolt_engine_owner
--   -v engine_migrator_role=NAME           migrator role, default reelbolt_engine_migrator
--   -v engine_connection_limit=N           runtime CONNECTION LIMIT, default 40 (see below)
--   -v engine_migrator_connection_limit=N  migrator CONNECTION LIMIT, default 4
--   -v engine_password=VALUE               sets the runtime role's password
--   -v engine_migrator_password=VALUE      sets the migrator's password and lets it log in
--
-- Never write a password literal here or on a command line that lands in shell history: pass it from
-- the environment (-v engine_password="$ENGINE_DB_PASSWORD"). The statement that carries it can reach
-- the server log when log_statement is ddl or all. Leave a password variable out to keep the current
-- password; a role with no password cannot log in over a password-authenticated connection until one
-- is set, for example with psql's \password (hashed client-side).
--
-- Safe to re-run: role creation is guarded, ALTER ROLE is idempotent, ownership is only changed where
-- it differs, and the grants are revoke-then-grant, so the roles converge to this file even when an
-- earlier revision granted more. Everything runs in one transaction with ON_ERROR_STOP on: a failure,
-- including the closing assertions, leaves nothing half applied. Nothing is ever dropped.
--
-- It works at every stage of a database's life, because every statement about a table is skipped
-- while that table does not exist:
--   * empty database: only the roles and the database and schema privileges apply;
--   * API migrated, engine not yet: the migrator then creates the engine's tables as the owner role;
--   * engine tables owned by a superuser (migrations run by a superuser, production before this
--     change) or by the runtime role (an earlier revision of this file): they are handed to the
--     owner role with ALTER TABLE ... OWNER TO.
-- All of these end in the same privilege state.
--
-- DENY BY DEFAULT, checked on every run (the closing block, identical in engine-role.sql,
-- gateway-role.sql and check-roles.sql). Code can hide on a table in many places, each runs with the
-- privileges of whoever writes the row (the Inference API's superuser login included), and each
-- survives a change of owner. So the check does not look for known-bad objects, it allows only what
-- the engine's migrations create. It FAILS, with one line per finding, when:
--   * a render-host role (engine runtime, gateway) has a privileged attribute, a role membership,
--     CREATE in any schema, CREATE or TEMPORARY on the database, or TRIGGER, REFERENCES or TRUNCATE on
--     any table;
--   * a render-host role or the migrator owns any relation, function, operator, schema or type;
--   * an event trigger exists that is not owned by a superuser;
--   * on an ENGINE table (the eleven named in the block, plus anything the owner role owns), whoever
--     owns it today: any rule, row security or policy, any trigger at all (the migrations create
--     none), any extended statistics, or a column default, generated expression, CHECK constraint,
--     index expression, index predicate or column type that depends on an object that is not built in
--     (oid 16384 and up: a function, operator, type or domain, collation, operator class), except a
--     sequence the table itself owns. Built-in functions such as now() and gen_random_uuid() pass;
--   * on any other table in public (the API's): a trigger function, or an object a default,
--     CHECK, index, column type, rule or policy depends on, that is owned by a non-superuser other
--     than the table's owner. What the API's own owner attaches to its tables is its business.
--   * a render-host role holds any table, column or sequence privilege beyond its grant list (the
--     'grants' list of engine-role.sql or gateway-role.sql, copied into the block), owns a large
--     object, or a role name given to the script does not exist (a misspelling fails, it does not pass);
--   * the owner role can log in, or the owner or migrator role has a privileged attribute or a
--     membership other than the migrator's in the owner role (pg_read_all_data and the other
--     predefined roles included).
-- Every script pins search_path to pg_catalog, pg_temp first thing in its transaction, so a function or
-- operator someone planted in public is never picked by this superuser session instead of the built-in.
--
-- Accepted residual risk (it needs row-level security or brokered writes, not grants): the engine's
-- runtime role writes credit and run rows (credit_accounts, credit_grants, api_key_spend, project_files,
-- workflow_executions) for every tenant, and the gateway role writes every row of platform_settings.
-- It can also UPDATE workflow_steps.video_compile_config_json and video_analyze_config_json, the
-- exact two columns ReviewAdjustableSettings.SetConfigJson writes when a review loop re-runs a step,
-- so it can rewrite those two configs on any tenant's step rows. A narrower grant needs a schema
-- change (review adjustments in a table of their own); until then this is accepted.
-- Nothing is cleaned or dropped automatically: the message names each object, and a person inspects
-- and removes it by hand. The read-only roles powerbi_ro and grafana_ro own nothing and attach nothing,
-- so they do not trip the check.
-- The hand-over of an engine table to the owner role is part of the same transaction as the check, and
-- the check inspects the engine tables by name whoever owns them: a default, rule, CHECK or index a
-- former owner planted stops the script, and the hand-over with it. Nothing is changed.
--
-- Runtime CONNECTION LIMIT 40. docs/database-connections.md: one engine pool is 12 connections, a
-- blue-green deploy overlaps two copies (24), a long drain can keep a third alive (36), and each
-- starting copy opens one more for the pending-migration check. Raise it with engine_connection_limit
-- when you run more engine replicas (12 per replica plus the overlap).
--
-- Two API-owned tables the runtime role can read hold protected material the engine needs to do its
-- job: inference_providers (encrypted provider API keys) and data_protection_keys (the key ring, read
-- only when DataProtection:Store=Database). Withholding either stops every provider call.
--
-- PUBLIC. "No grant" only means "no access" when PUBLIC holds nothing either, so this script revokes
-- from PUBLIC: every privilege on every table and sequence in schema public, CREATE on schema public,
-- and TEMPORARY on this database (neither the engine nor the gateway uses a temporary table, and a
-- temporary schema is a place to create functions). PostgreSQL grants PUBLIC no table privileges by
-- default and, from version 15, no CREATE on public, so on a current install only the TEMPORARY revoke
-- changes anything; the database owner and superusers keep TEMPORARY. When PUBLIC did hold CREATE (a
-- cluster created before version 15), the database owner is given USAGE and CREATE first so an owner
-- login keeps migrating. PUBLIC keeps what PostgreSQL gives it by default and this script does not
-- touch: CONNECT on the database, USAGE on schema public, EXECUTE on functions, USAGE on types.
--
-- The Inference API's own login is out of scope here: it stays the superuser or owner it is today.
-- One API migration alters an engine table: 20261004120000_AddDailiesRoomConfigJson runs
-- ALTER TABLE IF EXISTS workflow_steps ADD COLUMN IF NOT EXISTS .... With a superuser API login it
-- works as before. An operator whose API login is NOT a superuser, on a database where the engine
-- tables exist and that migration has not been applied yet, must either apply that one migration as a
-- superuser or make the API login a member of the owner role (GRANT reelbolt_engine_owner TO
-- <api login>) for that run, because only the owner may alter workflow_steps. On a fresh database the
-- API migrates first, the table does not exist yet and the statement does nothing.

\set ON_ERROR_STOP on

\if :{?engine_role}
\else
\set engine_role reelbolt_engine
\endif
\if :{?engine_owner_role}
\else
\set engine_owner_role reelbolt_engine_owner
\endif
\if :{?engine_migrator_role}
\else
\set engine_migrator_role reelbolt_engine_migrator
\endif
\if :{?engine_connection_limit}
\else
\set engine_connection_limit 40
\endif
\if :{?engine_migrator_connection_limit}
\else
\set engine_migrator_connection_limit 4
\endif
\if :{?engine_password}
\else
\set engine_password ''
\endif
\if :{?engine_migrator_password}
\else
\set engine_migrator_password ''
\endif

BEGIN;

-- Pin the search path before anything else runs: a role that can create in public must not be
-- able to plant an overload of a catalog function or operator that this superuser session would pick.
SET LOCAL search_path = pg_catalog, pg_temp;

-- Hand the psql variables to the DO blocks (psql does not substitute inside dollar quotes). The
-- settings are transaction-local. The passwords are not echoed: only whether one was supplied.
SELECT set_config('reelbolt.role', :'engine_role', true) AS engine_role;
SELECT set_config('reelbolt.owner_role', :'engine_owner_role', true) AS engine_owner_role;
SELECT set_config('reelbolt.migrator_role', :'engine_migrator_role', true) AS engine_migrator_role;
SELECT set_config('reelbolt.engine_role', :'engine_role', true) AS engine_role_checked;
SELECT set_config('reelbolt.assert_roles', :'engine_role', true) AS asserted_roles;
SELECT set_config('reelbolt.connection_limit', :'engine_connection_limit', true) AS connection_limit;
SELECT set_config('reelbolt.migrator_connection_limit', :'engine_migrator_connection_limit', true) AS migrator_connection_limit;
SELECT length(set_config('reelbolt.password', :'engine_password', true)) > 0 AS password_supplied;
SELECT length(set_config('reelbolt.migrator_password', :'engine_migrator_password', true)) > 0 AS migrator_password_supplied;

DO $$
DECLARE
  r text := current_setting('reelbolt.role');
  own text := current_setting('reelbolt.owner_role');
  mig text := current_setting('reelbolt.migrator_role');
  lim int := current_setting('reelbolt.connection_limit')::int;
  mlim int := current_setting('reelbolt.migrator_connection_limit')::int;
  pw text := current_setting('reelbolt.password');
  mpw text := current_setting('reelbolt.migrator_password');
  db text := current_database();
  role_oid oid;
  owner_oid oid;
  migrator_oid oid;
  who text;
  t text;
  spec text;
  privs text;
  o oid;
  seq oid;

  -- Tables the owner role owns: the engine's migrations create and alter them.
  owned text[] := ARRAY[
    'workflow_definitions',
    'workflow_steps',
    'workflow_executions',
    'workflow_step_results',
    'review_scores',
    'workflow_step_cache_entries',
    'decision_observations',
    'external_generation_jobs',
    'runner_jobs',
    'runner_sessions',
    '__EFMigrationsHistory_Workflow'
  ];

  -- What the owner role holds on API-owned tables: only what the engine's migrations need.
  owner_grants text[] := ARRAY[
    'projects: SELECT (id, organization_id), REFERENCES (id)',
    'agent_definitions: REFERENCES (id)'
  ];

  -- The runtime role, one table per line as 'table: privileges'. Every table WorkflowEngineDbContext
  -- maps must be here (EngineRoleSqlDriftTests fails the build otherwise). SELECT is what the engine
  -- reads; a write is listed only where engine code performs it, with the code that needs it.
  grants text[] := ARRAY[
    -- The engine's own tables (owned by the owner role).
    -- INSERT: ExecutionSnapshotter freezes a run's definition and steps. Never deleted here.
    'workflow_definitions: SELECT, INSERT',
    -- UPDATE of two columns: WorkflowExecutorService.ApplyReviewAdjustmentsAsync writes a review's adjusted
    -- settings to the run's own snapshot step (ReviewAdjustableSettings.SetConfigJson: compile and analyze).
    'workflow_steps: SELECT, INSERT, UPDATE (video_compile_config_json, video_analyze_config_json)',
    -- UPDATE: WorkflowExecutorService (status, progress), ExecutionLeaseService, ExecutionRecoverySweeper
    -- (FOR UPDATE SKIP LOCKED also needs UPDATE). The Inference API inserts and deletes runs.
    'workflow_executions: SELECT, UPDATE',
    -- INSERT, UPDATE: WorkflowExecutorService writes one row per step attempt and completes it.
    'workflow_step_results: SELECT, INSERT, UPDATE',
    -- INSERT: WorkflowExecutorService records review scores.
    'review_scores: SELECT, INSERT',
    -- INSERT, UPDATE, DELETE: StepResultCache (store, hit counters, eviction), StepCacheRetentionSweeper.
    'workflow_step_cache_entries: SELECT, INSERT, UPDATE, DELETE',
    -- INSERT, UPDATE: DecisionGate and CostRoutingGate record an observation and later its outcome.
    'decision_observations: SELECT, INSERT, UPDATE',
    -- INSERT, UPDATE: VideoGenerationBudgetGate reserves a job, VideoGenerateStepExecutor tracks it.
    'external_generation_jobs: SELECT, INSERT, UPDATE',
    -- Read only: the runner gateway writes these two; the engine reaches them through the gateway.
    'runner_jobs: SELECT',
    'runner_sessions: SELECT',
    -- Read only: the pending-migration check at startup (Database:MigrateAtStartup=false).
    '__EFMigrationsHistory_Workflow: SELECT',

    -- API-owned tables the engine writes.
    'project_files: SELECT, INSERT, UPDATE, DELETE',
    'media_derivatives: SELECT, INSERT, UPDATE',
    'composition_workspaces: SELECT, INSERT, UPDATE',
    'composition_workspace_versions: SELECT, INSERT',
    'composition_revisions: SELECT, UPDATE (result_version_id)',
    'edit_image_graphic_requests: SELECT, UPDATE (result_storage_key, result_width, result_height, result_offset_x, result_offset_y, result_workspace_id, result_version_id, result_version_number, result_composition_id, result_summary, result_model_name, result_recorded_at)',
    'content_moderation_decisions: SELECT, INSERT',
    'usage_events: SELECT, INSERT',
    'credit_accounts: SELECT, INSERT, UPDATE',
    'credit_ledger_entries: SELECT, INSERT',
    'credit_grants: SELECT, INSERT, UPDATE',
    'api_key_spend: SELECT, INSERT, UPDATE',
    -- column-level reads: password_hash and signup_ip are withheld
    'application_users: SELECT (id, email, display_name, created_at, is_admin, must_change_password, last_active_organization_id, email_verified_at, tokens_invalidated_at, deletion_requested_at, auth_provider, mcp_admin_tools_enabled, assistant_auto_approve, locale, is_service_account)',
    -- column-level reads: secret_hash is withheld
    'api_keys: SELECT (id, organization_id, created_by_user_id, service_user_id, name, key_id, last_four, scopes, project_ids, expires_at, revoked_at, revoked_by_user_id, daily_cap_millis, weekly_cap_millis, monthly_cap_millis, created_at, last_used_at, expiry_warned_7d_at, expiry_warned_1d_at)',
    -- read only
    'projects: SELECT',
    'agent_definitions: SELECT',
    'data_protection_keys: SELECT',
    'organizations: SELECT',
    'organization_memberships: SELECT',
    'organization_agent_provider_overrides: SELECT',
    'inference_providers: SELECT',
    'inference_models: SELECT',
    'model_capabilities: SELECT',
    'model_capability_prices: SELECT',
    'pricing_settings: SELECT',
    'rate_multipliers: SELECT',
    'plans: SELECT',
    'subscriptions: SELECT',
    'edit_timelines: SELECT',
    'edit_timeline_ops: SELECT',
    'edit_timeline_snapshots: SELECT',
    'media_bins: SELECT',
    'media_bin_items: SELECT',
    'media_analysis_results: SELECT',
    'project_voices: SELECT',
    'project_avatars: SELECT',
    'project_avatar_references: SELECT',
    'project_pronunciation_entries: SELECT',
    'sfx_library_items: SELECT',
    'user_video_spend_limits: SELECT',
    'design_systems: SELECT',
    'design_system_libraries: SELECT',
    'design_system_versions: SELECT',
    'step_change_requests: SELECT',
    'platform_settings: SELECT',
    'runner_devices: SELECT'
  ];

  -- Columns the runtime role must never be able to read, checked at the end.
  withheld text[] := ARRAY[
    'application_users.password_hash',
    'application_users.signup_ip',
    'api_keys.secret_hash'
  ];
  granted_tables text[] := ARRAY[]::text[];
BEGIN
  IF r = own OR r = mig OR own = mig THEN
    RAISE EXCEPTION 'engine_role, engine_owner_role and engine_migrator_role must be three different roles';
  END IF;
  FOREACH who IN ARRAY ARRAY[r, own, mig] LOOP
    IF who IN (current_user, session_user) THEN
      RAISE EXCEPTION 'role % is the login running this script; name dedicated engine roles', who;
    END IF;
  END LOOP;
  IF lim < 1 OR mlim < 1 THEN
    RAISE EXCEPTION 'connection limits must be 1 or more, got % and %', lim, mlim;
  END IF;

  -- 1. The roles and their attributes.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = own) THEN
    EXECUTE format('CREATE ROLE %I NOLOGIN', own);
  END IF;
  EXECUTE format(
    'ALTER ROLE %I WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', own);

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = mig) THEN
    EXECUTE format('CREATE ROLE %I NOLOGIN', mig);
  END IF;
  EXECUTE format(
    'ALTER ROLE %I WITH NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS INHERIT CONNECTION LIMIT %s',
    mig, mlim);
  IF mpw <> '' THEN
    EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', mig, mpw);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) THEN
    EXECUTE format('CREATE ROLE %I LOGIN', r);
  END IF;
  EXECUTE format(
    'ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT %s',
    r, lim);
  IF pw <> '' THEN
    EXECUTE format('ALTER ROLE %I PASSWORD %L', r, pw);
  END IF;

  SELECT oid INTO role_oid FROM pg_roles WHERE rolname = r;
  SELECT oid INTO owner_oid FROM pg_roles WHERE rolname = own;
  SELECT oid INTO migrator_oid FROM pg_roles WHERE rolname = mig;

  -- Memberships: the migrator is a member of the owner role and of nothing else; the other two are
  -- members of nothing. The migrator's sessions start as the owner role, so what it creates is the
  -- owner's.
  IF EXISTS (SELECT 1 FROM pg_auth_members WHERE member IN (role_oid, owner_oid))
     OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member = migrator_oid AND roleid <> owner_oid) THEN
    RAISE EXCEPTION 'roles %, % and % must hold only the memberships this script grants (the migrator in the owner role)', r, own, mig;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member = migrator_oid AND roleid = owner_oid) THEN
    EXECUTE format('GRANT %I TO %I', own, mig);
  END IF;
  EXECUTE format('ALTER ROLE %I SET role = %L', mig, own);

  -- 2. PUBLIC holds nothing on the tables and sequences, cannot create in the schema and cannot
  --    create temporary objects.
  IF has_schema_privilege('public', 'public', 'CREATE') THEN
    EXECUTE format('GRANT USAGE, CREATE ON SCHEMA public TO %I',
      (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = db));
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
  END IF;
  REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
  REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC;
  EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', db);

  -- 3. Database and schema. Only the owner role may create in the schema.
  FOREACH who IN ARRAY ARRAY[r, own, mig] LOOP
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM %I', db, who);
    EXECUTE format('REVOKE ALL ON SCHEMA public FROM %I', who);
  END LOOP;
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', db, r);
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', db, mig);
  EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', r);
  EXECUTE format('GRANT USAGE, CREATE ON SCHEMA public TO %I', own);

  -- 4. Ownership of the engine's tables goes to the owner role, where they exist and are not already
  --    its own (a superuser's, or the runtime role's from an earlier revision). Sequences and indexes
  --    that belong to a table follow it.
  FOREACH t IN ARRAY owned LOOP
    o := to_regclass(format('public.%I', t));
    IF o IS NOT NULL AND (SELECT relowner FROM pg_class WHERE oid = o) <> owner_oid THEN
      EXECUTE format('ALTER TABLE public.%I OWNER TO %I', t, own);
    END IF;
  END LOOP;

  -- 5. Reset: drop every privilege the three roles hold on tables and sequences they do not own, so a
  --    grant an earlier revision of this file made and this one withholds is gone. A table-level
  --    REVOKE also clears column-level grants.
  FOR o IN
    SELECT c.oid FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
  LOOP
    FOREACH who IN ARRAY ARRAY[r, own, mig] LOOP
      IF (SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = o) <> who THEN
        EXECUTE format('REVOKE ALL ON %s FROM %I', o::regclass, who);
      END IF;
    END LOOP;
  END LOOP;

  -- 6. The grants, each skipped while its table does not exist yet.
  FOREACH spec IN ARRAY owner_grants LOOP
    t := split_part(spec, ': ', 1);
    IF to_regclass(format('public.%I', t)) IS NOT NULL THEN
      EXECUTE format('GRANT %s ON TABLE public.%I TO %I', substr(spec, length(t) + 3), t, own);
    END IF;
  END LOOP;

  FOREACH spec IN ARRAY grants LOOP
    t := split_part(spec, ': ', 1);
    privs := substr(spec, length(t) + 3);
    granted_tables := granted_tables || t;
    o := to_regclass(format('public.%I', t));
    IF o IS NULL THEN
      CONTINUE;
    END IF;
    EXECUTE format('GRANT %s ON TABLE public.%I TO %I', privs, t, r);

    -- A sequence is granted only where an INSERT needs one (a serial or identity column).
    IF privs LIKE '%INSERT%' THEN
      FOR seq IN
        SELECT d.objid FROM pg_depend d
        JOIN pg_class s ON s.oid = d.objid AND s.relkind = 'S'
        WHERE d.refobjid = o AND d.refobjsubid > 0 AND d.deptype IN ('a', 'i')
      LOOP
        EXECUTE format('GRANT USAGE ON SEQUENCE %s TO %I', seq::regclass, r);
      END LOOP;
    END IF;
  END LOOP;

  -- 7. Assertions about this file's own lists. The role-wide assertions follow in the next block.
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r AND NOT rolcanlogin)
     OR EXISTS (SELECT 1 FROM pg_roles WHERE rolname = own AND rolcanlogin) THEN
    RAISE EXCEPTION 'role % must be able to log in and role % must not', r, own;
  END IF;

  FOREACH spec IN ARRAY withheld LOOP
    t := split_part(spec, '.', 1);
    IF EXISTS (
      SELECT 1 FROM information_schema.columns
      WHERE table_schema = 'public' AND table_name = t AND column_name = split_part(spec, '.', 2)
    ) AND has_column_privilege(r, format('public.%I', t), split_part(spec, '.', 2), 'SELECT') THEN
      RAISE EXCEPTION 'role % can read %, which must stay withheld', r, spec;
    END IF;
  END LOOP;

  FOR o, t IN
    SELECT c.oid, c.relname FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
  LOOP
    IF t = ANY (granted_tables) THEN
      CONTINUE;
    END IF;
    IF has_table_privilege(r, o, 'SELECT, INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER')
       OR has_any_column_privilege(r, o, 'SELECT, INSERT, UPDATE, REFERENCES') THEN
      RAISE EXCEPTION 'role % holds a privilege on %, which is outside the engine grant list', r, o::regclass;
    END IF;
  END LOOP;
END $$;

-- Closing assertions, the same block as in gateway-role.sql and check-roles.sql. A role that runs on
-- the render host must hold rows and nothing else. Any violation aborts the transaction, so nothing
-- above is applied either.
DO $$
DECLARE
  roles text[] := string_to_array(current_setting('reelbolt.assert_roles'), ',');
  owner_role text := current_setting('reelbolt.owner_role');
  migrator_role text := coalesce(current_setting('reelbolt.migrator_role', true), '');
  engine_role text := coalesce(current_setting('reelbolt.engine_role', true), '');
  gateway_role text := coalesce(current_setting('reelbolt.gateway_role', true), '');
  -- The privilege matrix of each render-host role: exactly the 'grants' list of engine-role.sql and of
  -- gateway-role.sql (EngineRoleSqlDriftTests). Any privilege beyond it is a finding, so the check run
  -- between releases sees a grant someone added by hand.
  engine_matrix text[] := ARRAY[
    'workflow_definitions: SELECT, INSERT',
    'workflow_steps: SELECT, INSERT, UPDATE (video_compile_config_json, video_analyze_config_json)',
    'workflow_executions: SELECT, UPDATE',
    'workflow_step_results: SELECT, INSERT, UPDATE',
    'review_scores: SELECT, INSERT',
    'workflow_step_cache_entries: SELECT, INSERT, UPDATE, DELETE',
    'decision_observations: SELECT, INSERT, UPDATE',
    'external_generation_jobs: SELECT, INSERT, UPDATE',
    'runner_jobs: SELECT',
    'runner_sessions: SELECT',
    '__EFMigrationsHistory_Workflow: SELECT',
    'project_files: SELECT, INSERT, UPDATE, DELETE',
    'media_derivatives: SELECT, INSERT, UPDATE',
    'composition_workspaces: SELECT, INSERT, UPDATE',
    'composition_workspace_versions: SELECT, INSERT',
    'composition_revisions: SELECT, UPDATE (result_version_id)',
    'edit_image_graphic_requests: SELECT, UPDATE (result_storage_key, result_width, result_height, result_offset_x, result_offset_y, result_workspace_id, result_version_id, result_version_number, result_composition_id, result_summary, result_model_name, result_recorded_at)',
    'content_moderation_decisions: SELECT, INSERT',
    'usage_events: SELECT, INSERT',
    'credit_accounts: SELECT, INSERT, UPDATE',
    'credit_ledger_entries: SELECT, INSERT',
    'credit_grants: SELECT, INSERT, UPDATE',
    'api_key_spend: SELECT, INSERT, UPDATE',
    'application_users: SELECT (id, email, display_name, created_at, is_admin, must_change_password, last_active_organization_id, email_verified_at, tokens_invalidated_at, deletion_requested_at, auth_provider, mcp_admin_tools_enabled, assistant_auto_approve, locale, is_service_account)',
    'api_keys: SELECT (id, organization_id, created_by_user_id, service_user_id, name, key_id, last_four, scopes, project_ids, expires_at, revoked_at, revoked_by_user_id, daily_cap_millis, weekly_cap_millis, monthly_cap_millis, created_at, last_used_at, expiry_warned_7d_at, expiry_warned_1d_at)',
    'projects: SELECT',
    'agent_definitions: SELECT',
    'data_protection_keys: SELECT',
    'organizations: SELECT',
    'organization_memberships: SELECT',
    'organization_agent_provider_overrides: SELECT',
    'inference_providers: SELECT',
    'inference_models: SELECT',
    'model_capabilities: SELECT',
    'model_capability_prices: SELECT',
    'pricing_settings: SELECT',
    'rate_multipliers: SELECT',
    'plans: SELECT',
    'subscriptions: SELECT',
    'edit_timelines: SELECT',
    'edit_timeline_ops: SELECT',
    'edit_timeline_snapshots: SELECT',
    'media_bins: SELECT',
    'media_bin_items: SELECT',
    'media_analysis_results: SELECT',
    'project_voices: SELECT',
    'project_avatars: SELECT',
    'project_avatar_references: SELECT',
    'project_pronunciation_entries: SELECT',
    'sfx_library_items: SELECT',
    'user_video_spend_limits: SELECT',
    'design_systems: SELECT',
    'design_system_libraries: SELECT',
    'design_system_versions: SELECT',
    'step_change_requests: SELECT',
    'platform_settings: SELECT',
    'runner_devices: SELECT'
  ];
  gateway_matrix text[] := ARRAY[
    'runner_devices: SELECT, INSERT, UPDATE',
    'runner_jobs: SELECT, INSERT, UPDATE',
    'runner_sessions: SELECT, INSERT, UPDATE',
    'platform_settings: SELECT, INSERT, UPDATE',
    'workflow_executions: SELECT (id, status, workflow_definition_id, organization_id)',
    'workflow_definitions: SELECT (id, name, project_id)',
    'projects: SELECT (id, name, organization_id)',
    'organizations: SELECT (id, name)'
  ];
  matrix text[];
  spec text;
  m text[];
  exp_tbl text[];
  exp_col text[];
  p text;
  seq oid;
  -- The engine's tables, by name: they are checked whoever owns them today, so a table that is about
  -- to be handed to the owner role is inspected before the hand-over can take effect. Keep this list
  -- equal to the 'owned' list of engine-role.sql (EngineRoleSqlDriftTests).
  engine_tables text[] := ARRAY[
    'workflow_definitions',
    'workflow_steps',
    'workflow_executions',
    'workflow_step_results',
    'review_scores',
    'workflow_step_cache_entries',
    'decision_observations',
    'external_generation_jobs',
    'runner_jobs',
    'runner_sessions',
    '__EFMigrationsHistory_Workflow'
  ];
  engine_oids oid[];
  api_oids oid[];
  r text;
  rid oid;
  found text;
  tbl oid;
  deny_all boolean;
  tname text;
  towner oid;
  rec record;
  violations text[] := ARRAY[]::text[];
BEGIN
  -- A. The roles the render host logs in as hold rows and nothing else.
  FOREACH r IN ARRAY roles LOOP
    SELECT oid INTO rid FROM pg_roles WHERE rolname = r;
    IF rid IS NULL THEN
      violations := violations || format('role %s does not exist (a misspelled name, or engine-role.sql and gateway-role.sql have not run)', r);
      CONTINUE;
    END IF;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE oid = rid
               AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
      violations := violations || format('%s has a privileged role attribute (superuser, createdb, createrole, replication or bypassrls)', r);
    END IF;

    SELECT string_agg(pg_get_userbyid(roleid), ', ') INTO found FROM pg_auth_members WHERE member = rid;
    IF found IS NOT NULL THEN
      violations := violations || format('%s is a member of: %s', r, found);
    END IF;

    SELECT string_agg(n.nspname, ', ') INTO found FROM pg_namespace n WHERE has_schema_privilege(rid, n.oid, 'CREATE');
    IF found IS NOT NULL THEN
      violations := violations || format('%s can create objects in schema: %s', r, found);
    END IF;
    IF has_database_privilege(rid, current_database(), 'CREATE')
       OR has_database_privilege(rid, current_database(), 'TEMPORARY') THEN
      violations := violations || format('%s holds CREATE or TEMPORARY on database %s', r, current_database());
    END IF;

    SELECT string_agg(c.oid::regclass::text, ', ') INTO found FROM pg_class c
     WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
       AND (has_table_privilege(rid, c.oid, 'TRIGGER, REFERENCES, TRUNCATE')
            OR has_any_column_privilege(rid, c.oid, 'REFERENCES'));
    IF found IS NOT NULL THEN
      violations := violations || format('%s holds TRIGGER, REFERENCES or TRUNCATE on: %s', r, found);
    END IF;

    SELECT string_agg(l.oid::text, ', ') INTO found FROM pg_largeobject_metadata l WHERE l.lomowner = rid;
    IF found IS NOT NULL THEN
      violations := violations || format('%s owns large objects: %s', r, found);
    END IF;

    -- Nothing beyond the role's matrix: table and column privileges, and sequences (USAGE only on a
    -- sequence of a table the matrix lets it INSERT into).
    matrix := CASE r WHEN engine_role THEN engine_matrix WHEN gateway_role THEN gateway_matrix END;
    IF matrix IS NULL THEN
      violations := violations || format('%s is neither the engine role nor the gateway role: no privilege matrix to check it against', r);
      CONTINUE;
    END IF;
    FOR tbl, tname IN
      SELECT c.oid, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
       WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'f')
    LOOP
      exp_tbl := ARRAY[]::text[];
      exp_col := ARRAY[]::text[];
      FOREACH spec IN ARRAY matrix LOOP
        CONTINUE WHEN split_part(spec, ': ', 1) <> tname;
        FOR m IN SELECT regexp_matches(substr(spec, length(tname) + 3),
                   '(SELECT|INSERT|UPDATE|DELETE|TRUNCATE|REFERENCES|TRIGGER)(?:\s*\(([^)]*)\))?', 'g') LOOP
          IF m[2] IS NULL THEN
            exp_tbl := exp_tbl || m[1];
          ELSE
            exp_col := exp_col || ARRAY(SELECT m[1] || ':' || btrim(x) FROM unnest(string_to_array(m[2], ',')) x);
          END IF;
        END LOOP;
      END LOOP;
      FOREACH p IN ARRAY ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE', 'REFERENCES', 'TRIGGER'] LOOP
        IF has_table_privilege(rid, tbl, p) THEN
          IF NOT p = ANY (exp_tbl) THEN
            violations := violations || format('%s holds %s on %s, which is outside its grant list', r, p, quote_ident(tname));
          END IF;
        ELSIF p IN ('SELECT', 'INSERT', 'UPDATE', 'REFERENCES') THEN
          SELECT string_agg(quote_ident(a.attname), ', ' ORDER BY a.attnum) INTO found FROM pg_attribute a
           WHERE a.attrelid = tbl AND a.attnum > 0 AND NOT a.attisdropped
             AND has_column_privilege(rid, tbl, a.attnum, p)
             AND NOT (p || ':' || a.attname) = ANY (exp_col);
          IF found IS NOT NULL THEN
            violations := violations || format('%s holds %s (%s) on %s, which is outside its grant list', r, p, found, quote_ident(tname));
          END IF;
        END IF;
      END LOOP;
    END LOOP;
    FOR seq, tname IN
      SELECT s.oid, (SELECT t.relname FROM pg_depend d JOIN pg_class t ON t.oid = d.refobjid
                      WHERE d.classid = 'pg_class'::regclass AND d.objid = s.oid
                        AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i') LIMIT 1)
        FROM pg_class s JOIN pg_namespace n ON n.oid = s.relnamespace
       WHERE n.nspname = 'public' AND s.relkind = 'S'
    LOOP
      IF has_sequence_privilege(rid, seq, 'SELECT') OR has_sequence_privilege(rid, seq, 'UPDATE') THEN
        violations := violations || format('%s holds SELECT or UPDATE on sequence %s', r, seq::regclass);
      ELSIF has_sequence_privilege(rid, seq, 'USAGE') AND NOT EXISTS (
          SELECT 1 FROM unnest(matrix) s WHERE split_part(s, ': ', 1) = tname AND s ~ '\mINSERT\M') THEN
        violations := violations || format('%s holds USAGE on sequence %s, which no INSERT of its grant list needs', r, seq::regclass);
      END IF;
    END LOOP;
  END LOOP;

  -- The owner role logs in never; the owner and the migrator hold no privileged attribute; the
  -- migrator is a member of the owner role only and the owner role of nothing (a predefined role
  -- such as pg_read_all_data or pg_execute_server_program included).
  FOREACH r IN ARRAY ARRAY[owner_role, migrator_role] LOOP
    SELECT oid INTO rid FROM pg_roles WHERE rolname = r;
    IF rid IS NULL THEN
      violations := violations || format('role %s does not exist (a misspelled name, or engine-role.sql has not run)', r);
      CONTINUE;
    END IF;
    IF EXISTS (SELECT 1 FROM pg_roles WHERE oid = rid
               AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)) THEN
      violations := violations || format('%s has a privileged role attribute (superuser, createdb, createrole, replication or bypassrls)', r);
    END IF;
    SELECT string_agg(pg_get_userbyid(roleid), ', ') INTO found FROM pg_auth_members
     WHERE member = rid AND NOT (r = migrator_role AND pg_get_userbyid(roleid) = owner_role);
    IF found IS NOT NULL THEN
      violations := violations || format('%s is a member of: %s', r, found);
    END IF;
  END LOOP;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = owner_role AND rolcanlogin) THEN
    violations := violations || format('%s can log in; the owner role must be NOLOGIN', owner_role);
  END IF;

  -- B. No login role owns anything: not the render host's roles, and not the migrator (its sessions
  --    act as the owner role, so an object in its own name was not made by a migration).
  FOREACH r IN ARRAY roles || migrator_role LOOP
    SELECT oid INTO rid FROM pg_roles WHERE rolname = r;
    IF rid IS NULL THEN
      CONTINUE;
    END IF;
    SELECT string_agg(x, ', ') INTO found FROM (
      SELECT 'relation ' || c.oid::regclass::text AS x FROM pg_class c WHERE c.relowner = rid
      UNION ALL
      SELECT 'function ' || p.oid::regprocedure::text FROM pg_proc p WHERE p.proowner = rid
      UNION ALL
      SELECT 'operator ' || o.oid::regoperator::text FROM pg_operator o WHERE o.oprowner = rid
      UNION ALL
      SELECT 'schema ' || n.nspname FROM pg_namespace n WHERE n.nspowner = rid
      UNION ALL
      SELECT 'type ' || y.oid::regtype::text FROM pg_type y WHERE y.typowner = rid
    ) owned_objects;
    IF found IS NOT NULL THEN
      violations := violations || format('%s owns: %s', r, found);
    END IF;
  END LOOP;

  -- C. An event trigger runs its function for every login's DDL.
  SELECT string_agg(format('%s (owned by %s)', e.evtname, o.rolname), ', ') INTO found
    FROM pg_event_trigger e JOIN pg_roles o ON o.oid = e.evtowner WHERE NOT o.rolsuper;
  IF found IS NOT NULL THEN
    violations := violations || format('event trigger not owned by a superuser: %s', found);
  END IF;

  -- D. Code can hide on a table in more places than a trigger: a rule, a row security policy, a
  --    column default or generated expression, a CHECK constraint, an index expression or predicate,
  --    a column whose type is a domain. Each runs with the privileges of whoever writes the row, the
  --    superuser API login included, and each survives a change of owner.
  --      Engine tables: DENY BY DEFAULT. The engine's migrations create no rule, policy or trigger
  --      and use only built-in functions, operators and types, so anything else is a finding: any
  --      rule, row security, any trigger, any extended statistics, and any dependency of a default,
  --      generated column, CHECK, index or column type on an object that is not built in (oid 16384
  --      and up), except a sequence the table itself owns.
  --      Every other table in public (the API's): the API owns them and may attach what it likes, so
  --      only an object owned by a non-superuser other than the table's owner is a finding. Checked on
  --      all of them, not only those a render-host role writes, so each script finds the same.
  SELECT coalesce(array_agg(c.oid), '{}') INTO engine_oids
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
     AND (c.relname = ANY (engine_tables) OR pg_get_userbyid(c.relowner) = owner_role
          OR pg_get_userbyid(c.relowner) = ANY (roles || migrator_role));
  SELECT coalesce(array_agg(c.oid), '{}') INTO api_oids
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')
     AND NOT (c.oid = ANY (engine_oids));

  FOR tbl, deny_all IN
    SELECT unnest(engine_oids), true UNION ALL SELECT unnest(api_oids), false
  LOOP
    tname := (SELECT quote_ident(relname) FROM pg_class WHERE oid = tbl);
    SELECT relowner INTO towner FROM pg_class WHERE oid = tbl;

    IF deny_all THEN
      FOR rec IN SELECT rulename FROM pg_rewrite WHERE ev_class = tbl AND rulename <> '_RETURN' LOOP
        violations := violations || format('rule %s on %s', rec.rulename, tname);
      END LOOP;
      IF EXISTS (SELECT 1 FROM pg_class WHERE oid = tbl AND (relrowsecurity OR relforcerowsecurity)) THEN
        violations := violations || format('row security is enabled on %s', tname);
      END IF;
      FOR rec IN SELECT polname FROM pg_policy WHERE polrelid = tbl LOOP
        violations := violations || format('policy %s on %s', rec.polname, tname);
      END LOOP;
      FOR rec IN SELECT stxname FROM pg_statistic_ext WHERE stxrelid = tbl LOOP
        violations := violations || format('extended statistics %s on %s', rec.stxname, tname);
      END LOOP;
    END IF;

    FOR rec IN
      SELECT g.tgname, g.tgfoid::regprocedure::text AS fn, fo.rolname AS fn_owner, fo.rolsuper, p.proowner
        FROM pg_trigger g
        JOIN pg_proc p ON p.oid = g.tgfoid
        JOIN pg_roles fo ON fo.oid = p.proowner
       WHERE g.tgrelid = tbl AND NOT g.tgisinternal
    LOOP
      IF deny_all OR (NOT rec.rolsuper AND rec.proowner <> towner) THEN
        violations := violations || format('trigger %s on %s (function %s, owned by %s)', rec.tgname, tname, rec.fn, rec.fn_owner);
      END IF;
    END LOOP;

    FOR rec IN
      SELECT src.what,
             pg_describe_object(d.refclassid, d.refobjid, d.refobjsubid) AS target,
             ro.rolname AS target_owner, ro.rolsuper AS target_super, ro.oid AS target_owner_oid
        FROM (
          SELECT 'pg_attrdef'::regclass::oid AS classid, a.oid AS objid,
                 format('default or generated expression of column %s',
                   (SELECT attname FROM pg_attribute WHERE attrelid = a.adrelid AND attnum = a.adnum)) AS what
            FROM pg_attrdef a WHERE a.adrelid = tbl
          UNION ALL
          SELECT 'pg_constraint'::regclass::oid, k.oid, format('CHECK constraint %s', k.conname)
            FROM pg_constraint k WHERE k.conrelid = tbl AND k.contype = 'c'
          UNION ALL
          SELECT 'pg_class'::regclass::oid, i.indexrelid, format('index %s', (SELECT quote_ident(relname) FROM pg_class WHERE oid = i.indexrelid))
            FROM pg_index i WHERE i.indrelid = tbl
          UNION ALL
          SELECT 'pg_class'::regclass::oid, tbl, 'a column type'
          UNION ALL
          SELECT 'pg_rewrite'::regclass::oid, w.oid, format('rule %s', w.rulename)
            FROM pg_rewrite w WHERE w.ev_class = tbl
          UNION ALL
          SELECT 'pg_policy'::regclass::oid, y.oid, format('policy %s', y.polname)
            FROM pg_policy y WHERE y.polrelid = tbl
        ) src
        JOIN pg_depend d ON d.classid = src.classid AND d.objid = src.objid
        LEFT JOIN pg_roles ro ON ro.oid = CASE d.refclassid
          WHEN 'pg_proc'::regclass THEN (SELECT proowner FROM pg_proc WHERE oid = d.refobjid)
          WHEN 'pg_type'::regclass THEN (SELECT typowner FROM pg_type WHERE oid = d.refobjid)
          WHEN 'pg_class'::regclass THEN (SELECT relowner FROM pg_class WHERE oid = d.refobjid)
          WHEN 'pg_operator'::regclass THEN (SELECT oprowner FROM pg_operator WHERE oid = d.refobjid)
          WHEN 'pg_collation'::regclass THEN (SELECT collowner FROM pg_collation WHERE oid = d.refobjid)
          WHEN 'pg_opclass'::regclass THEN (SELECT opcowner FROM pg_opclass WHERE oid = d.refobjid)
          WHEN 'pg_namespace'::regclass THEN (SELECT nspowner FROM pg_namespace WHERE oid = d.refobjid)
        END
       WHERE d.deptype = 'n'
         AND d.refobjid >= 16384
         AND NOT (d.refclassid = 'pg_class'::regclass AND d.refobjid = tbl)
         AND NOT (d.refclassid = 'pg_class'::regclass AND EXISTS (
               SELECT 1 FROM pg_depend s
                WHERE s.classid = 'pg_class'::regclass AND s.objid = d.refobjid
                  AND s.refclassid = 'pg_class'::regclass AND s.refobjid = tbl AND s.deptype IN ('a', 'i')))
    LOOP
      IF deny_all THEN
        violations := violations || format('%s on %s uses %s, which is not built in', rec.what, tname, rec.target);
      ELSIF rec.target_owner IS NOT NULL AND NOT rec.target_super AND rec.target_owner_oid <> towner THEN
        violations := violations || format('%s on %s uses %s, owned by %s', rec.what, tname, rec.target, rec.target_owner);
      END IF;
    END LOOP;
  END LOOP;

  IF cardinality(violations) > 0 THEN
    RAISE EXCEPTION E'least-privilege check failed, nothing was changed:\n  %\nInspect each object and remove it by hand (nothing is cleaned automatically), then run this again.',
      array_to_string(violations, E'\n  ');
  END IF;
END $$;

COMMIT;
