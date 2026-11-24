-- ReelBolt least-privilege role check (read-only).
--
-- Reports whether the two logins the render host holds, the engine's runtime role and the runner
-- gateway's role, still hold rows and nothing else. It changes nothing (one READ ONLY transaction), so
-- an operator or a monitor can run it at any time, as a superuser:
--
--   psql "$DATABASE_URL" -f infra/postgres/check-roles.sql
--
-- It exits non-zero (ON_ERROR_STOP) with one line per finding.
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
-- Nothing is cleaned or dropped automatically: the message names each object, and a person inspects
-- and removes it by hand. The read-only roles powerbi_ro and grafana_ro own nothing and attach nothing,
-- so they do not trip the check.
--
-- These are the closing assertions of engine-role.sql and gateway-role.sql, which also refuse to
-- apply while one fails. A role that does not exist is reported with a NOTICE and not counted.
--
-- psql variables (all optional):
--
--   -v engine_role=NAME        default reelbolt_engine
--   -v gateway_role=NAME       default reelbolt_gateway
--   -v engine_owner_role=NAME  default reelbolt_engine_owner
--   -v engine_migrator_role=NAME  default reelbolt_engine_migrator

\set ON_ERROR_STOP on

\if :{?engine_role}
\else
\set engine_role reelbolt_engine
\endif
\if :{?gateway_role}
\else
\set gateway_role reelbolt_gateway
\endif
\if :{?engine_migrator_role}
\else
\set engine_migrator_role reelbolt_engine_migrator
\endif
\if :{?engine_owner_role}
\else
\set engine_owner_role reelbolt_engine_owner
\endif

BEGIN TRANSACTION READ ONLY;

-- Pin the search path before anything else runs: a role that can create in public must not be
-- able to plant an overload of a catalog function or operator that this superuser session would pick.
SET LOCAL search_path = pg_catalog, pg_temp;

SELECT set_config('reelbolt.engine_role', :'engine_role', true) AS engine_role_checked;
SELECT set_config('reelbolt.gateway_role', :'gateway_role', true) AS gateway_role_checked;
SELECT set_config('reelbolt.assert_roles', concat(:'engine_role', ',', :'gateway_role'), true) AS checked_roles;
SELECT set_config('reelbolt.owner_role', :'engine_owner_role', true) AS engine_owner_role;
SELECT set_config('reelbolt.migrator_role', :'engine_migrator_role', true) AS engine_migrator_role;

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

SELECT 'ok: the checked roles hold rows and nothing else' AS result;

COMMIT;
