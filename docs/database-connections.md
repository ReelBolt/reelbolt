# Database connections

Operator and developer reference for how many Postgres connections each ReelBolt service may open,
how those caps add up against the server's `max_connections`, how to change them, which metrics
and alerts watch them, and what has to change before a connection pooler such as PgBouncer can sit
in front of Postgres. This page is internal and is not published on the docs site.

## Pool sizes per service

Four services connect to the shared Postgres, and each one caps its own connection pool. The
defaults below are in the code and repeated in `docker-compose.yml` and `.env.example`.

| Service | Driver | Setting read by the service | Compose / `.env` variable | Default |
|---|---|---|---|---|
| Inference API | Npgsql (EF Core) | `Database:MaxPoolSize` (env `Database__MaxPoolSize`) | `INFERENCE_DB_MAX_POOL_SIZE` | 16 |
| Workflow engine | Npgsql (EF Core) | `Database:MaxPoolSize` (env `Database__MaxPoolSize`) | `ENGINE_DB_MAX_POOL_SIZE` | 12 |
| Go API | GORM over `database/sql` (pgx) | `DB_MAX_OPEN_CONNS` / `DB_MAX_IDLE_CONNS` / `DB_CONN_MAX_LIFETIME` | `GO_API_DB_MAX_OPEN_CONNS` / `GO_API_DB_MAX_IDLE_CONNS` / `GO_API_DB_CONN_MAX_LIFETIME` | 8 / 4 / `30m` |
| Runner gateway | pgx (`pgxpool`) | `DATABASE_MAX_CONNS`, per pool | `RUNNER_GATEWAY_DB_MAX_CONNS` | 4 per pool x 3 pools = 12 |

The runner gateway opens three pools over `DATABASE_URL` (`devices`, `jobs` and `switches`), and
`DATABASE_MAX_CONNS` caps each of them, so one gateway holds at most three times that value. The
floor is 2: the `jobs` pool keeps one connection open permanently for `LISTEN runner_jobs_new`, so
with a cap of 1 the queue itself would get no connection.

How each service picks its value:

- **Precedence.** An explicit setting wins; otherwise a value already in the connection string or
  URL is kept; otherwise the default applies. For the .NET services that is `Database:MaxPoolSize`,
  then `Maximum Pool Size` in `ConnectionStrings:DefaultConnection`, then 16 or 12
  (`PostgresPoolSizing.ApplyMaxPoolSize` in `inference/src/ReelBolt.Shared/Data/PostgresPoolSizing.cs`).
  For the runner gateway it is `DATABASE_MAX_CONNS`, then `pool_max_conns` in `DATABASE_URL`, then 4
  (`sandbox/cmd/runner-gateway/pool.go`). The Go API reads only its environment variables, then the
  defaults (`parseDBPool` in `api/config/config.go`).
- **Compose always sets the explicit value.** Every compose file that runs these services sets
  `Database__MaxPoolSize` and `DATABASE_MAX_CONNS` with a default (for example
  `${INFERENCE_DB_MAX_POOL_SIZE:-16}`), so in the compose stack the environment variable always wins
  over a `Maximum Pool Size` in `ConnectionStrings__DefaultConnection` (`DATABASE_CONNECTION_STRING`
  in the cloud compose files) or a `pool_max_conns` in `DATABASE_URL`, and nothing is logged. To use
  the value in the connection string or URL, remove that service's `Database__MaxPoolSize` or
  `DATABASE_MAX_CONNS` line from the compose file.
- **Bad values fail startup.** A non-integer or a value of 0 or less in `Database:MaxPoolSize`,
  `DB_MAX_OPEN_CONNS` or `DB_MAX_IDLE_CONNS`, a `DB_CONN_MAX_LIFETIME` that is not a positive Go
  duration, or a `DATABASE_MAX_CONNS` below 2 stops the service at startup instead of falling back
  to a default. For the .NET services, a pool size already in the connection string must also be a
  whole number greater than 0, or startup fails, even when `Database:MaxPoolSize` overrides it. A
  `Minimum Pool Size` (or `Min Pool Size`, `MinPoolSize`) in the connection string must be a whole
  number of 0 or more and no greater than the effective maximum (equal is fine), or startup fails
  naming both settings and both numbers. The Inference API also fails startup on
  `Billing:ChangeLock:MaxConnections` below 1 or `Billing:ChangeLock:SlotWaitMilliseconds` below 0;
  the message names only the key. The Go API clamps `DB_MAX_IDLE_CONNS` down to `DB_MAX_OPEN_CONNS` with a log line rather than failing.
- **Npgsql keyword spelling.** Npgsql 10 accepts `Maximum Pool Size` and `MaxPoolSize` in a
  connection string and rejects `Max Pool Size` (the SqlClient spelling); likewise it accepts
  `Minimum Pool Size` and `MinPoolSize` and rejects `Min Pool Size`. The helper recognises all three
  spellings of each, in any case, and writes back exactly one `Maximum Pool Size` and, when a minimum
  was given, one `Minimum Pool Size`. The inference API's connection sampler and the startup bootstrap
  accept every spelling too: the sampler replaces them with its own one-connection cap and a minimum
  of 0, and the bootstrap (unpooled) removes them.

To change a cap in the compose stack, set the variable in `.env` and recreate the service:

```bash
echo 'ENGINE_DB_MAX_POOL_SIZE=10' >> .env
docker compose up -d workflow-engine
```

Redo the budget arithmetic below before you raise any cap.

## Connection budget arithmetic

The sum of all pool caps has to fit under the Postgres server's `max_connections`, with room left
for administrative sessions. With the defaults and one replica of each service:

| Item | Connections |
|---|---|
| Inference API pool + sampler connection | 16 + 1 |
| Workflow engine pool | 12 |
| Go API pool | 8 |
| Runner gateway pools (3 x 4) | 12 |
| **Pooled total** | **49** |
| `BillingChangeLock` claims | At most `Billing:ChangeLock:MaxConnections` (default 4) per Inference API replica, unpooled (see below) |
| `superuser_reserved_connections` | 3 |
| `max_connections` (compose `postgres:16-alpine` default) | 100 |

**Overlap during a blue-green deploy.** While an old slot drains, two Inference API pools and two engine pools
exist at once, and the old copy's pools count until it is stopped. the operator release script uses 84 as the sum of the pool caps at full overlap (the
constant `POOL_CAP_SUM`: two copies of the Inference API, engine and Go API pools, `2 x (16 + 12 + 8)`, plus the one runner gateway's 12; it is not recomputed from `.env`). With the two sampler connections and the two replicas' billing claims (at most 4 each) the worst case is 94, under the 97 connections a client may use (`max_connections` 100 minus 3 reserved). an operator file pins the defaults, `POOL_CAP_SUM` and this bound together, so raising one cap fails the test until the budget is redone. The bound leaves out the optional Grafana service of the observability profile (up to 4 connections, which brings the worst case to 98) and a third slot kept alive by a long drain (the cap sum is then 120, and the deploy's warning still uses the fixed 84): in either case check `max_connections` and the pool caps by hand first.
the operator release script reads `max_connections` before starting a slot and prints a warning when it is below
`REELBOLT_POOL_CAP_SUM` (default 84); raising the limit needs a Postgres restart. See
the operator runbook.

`BillingChangeLock` (`inference/src/ReelBolt.Inference.Api/Services/Billing/BillingChangeLock.cs`)
takes an organization's claim with a session-level `pg_try_advisory_lock` on its own unpooled
connection, and holds that connection until the claim ends. Those connections carry
`Application Name=reelbolt-billing-lock`, so they show up under that name in `pg_stat_activity`.

Each process caps these connections at `Billing:ChangeLock:MaxConnections` (default 4), across all
organizations. An attempt takes one slot before it connects, and waits at most
`Billing:ChangeLock:SlotWaitMilliseconds` (default 2000; 0 means do not wait) for a free one. A
slot is returned when the claim is disposed, when the attempt is refused, and when it throws. The
cases are:

- **No slot in time.** Nothing connects. On seats, change-plan, cancel and resume the answer is
  `503 billing_busy` with `Retry-After: 5`. The body is
  `{"error":"billing_busy","message":"Billing is busy right now. Try again in a few seconds."}`.
- **Same organization already claimed in this process.** The attempt is refused before it takes a
  slot or connects. The answer is `409 change_pending`, as before.
- **Checkout heal with no slot.** The heal is skipped, and checkout answers the existing
  `409 subscription_pending`.
- **Reconcile with no slot** (the sweep, the admin resync, or a lazy heal). The reconcile returns
  `ReconcileOutcome.Busy`, and the sweep counts it as a busy run. The admin resync answers its
  existing `409 change_in_progress`.

A losing contender, one whose advisory lock another session already holds, still opens a connection
briefly before it is refused. That connection uses a slot too.

Every `IBillingChangeLock` taker counts:

- The billing requests in `BillingController` (seats, change-plan, cancel, resume) each take the
  claim before the live provider read. They hold it until the whole change returns: the provider's
  PATCH or POST, and any lazy heal and rerun under the same claim. Cancel and resume lazy-heal
  under that held claim too. They take it before they check that the organization has a
  subscription, so those calls open a connection too.
- The lazy heal: a change refused because Paddle's live state disagrees with the row reconciles
  under the claim its request already holds. It opens no second connection.
- The checkout heal: checkout, when Paddle already has a live subscription, takes its own claim and
  reconciles under it.
- The admin resync (`BillingAdminController`, platform admins only) takes the claim for one
  reconcile.
- The #339 reconcile sweep (`BillingReconcileSweeper`) reconciles its candidates one after another.
  Each replica holds at most one claim at a time, for up to `MaxPerSweep` (default 50) organizations
  per pass. The first pass runs `Billing:Reconcile:StartupDelaySeconds` (default 60) after start.
  After each pass ends, the sweep waits `Billing:Reconcile:SweepIntervalSeconds` (default 300) before
  the next one starts. The wait is measured from the end of a pass, not from its start.

Every reconcile (`BillingReconciler`) holds the claim connection across its Paddle API reads, and
each read is bounded by the Paddle `HttpClient` timeout of 30 s, so a claim is held for as long as its
reads take. While a replica's slots are all held, further attempts wait up to
`Billing:ChangeLock:SlotWaitMilliseconds` and then get the refusals listed above. The number of
lock connections per replica never exceeds `Billing:ChangeLock:MaxConnections`.

The Inference API's server connection sampler uses its own one-connection data source, so it adds
one connection beside the API's pool (see [Connection metrics](#connection-metrics)).

The startup bootstrap that creates a missing database (see [Connection metrics](#connection-metrics))
does not change the sum: it is transient and runs at startup only, before migrations, holding at most
one unpooled connection at a time and closing it before the service's pool is used.

That leaves room for `psql`, migrations (on a two-box layout the control host's one-shot
`engine-migrator` migrates the engine's tables; the media engine runs with
`Database__MigrateAtStartup=false`), one-off commands such as `reelbolt-worker enroll`, and one
extra engine replica: 49 + 12 = 61.

For N replicas of each service:

```text
pooled = (16 + 1) x N_inference + 12 x N_engine + 8 x N_goapi + 3 x 4 x N_gateway
billing claims <= 4 x N_inference                  (Billing:ChangeLock:MaxConnections, default 4)
pooled <= 0.8 x max_connections
pooled + billing claims + superuser_reserved_connections < max_connections
```

Keep the pooled total at or below about 80% of `max_connections`; the rest is for the unpooled
billing claims, the reserved superuser slots and maintenance sessions. With `max_connections` 100,
that is 80: two extra engine replicas fit (61, 73), a third one (85) does not.

On the control VM's Postgres container, `max_connections` depends on the container's configuration,
so read it from the running server and redo the sum:

```sql
SHOW max_connections;
SHOW superuser_reserved_connections;
```

## Connection metrics

Each service publishes its pool state, and the Inference API also samples the server-wide count.
The OpenTelemetry metrics leave the process only when OTLP export is on
([`observability.md`](observability.md#turning-export-on)); the runner gateway's metrics are
Prometheus gauges on its `/metrics` endpoint.

| Source | Meter or endpoint | Instruments |
|---|---|---|
| Inference API, workflow engine | meter `Npgsql` | `db.client.connection.count{db.client.connection.state=used\|idle}`, `db.client.connection.max`, `db.client.connection.npgsql.pending_requests`, `db.client.connection.npgsql.timeouts` |
| Go API | meter `ReelBolt.GoApi` | `db.client.connection.count{db.client.connection.state=used\|idle}`, `db.client.connection.max`, `db.client.connection.wait_count` (cumulative) |
| Runner gateway | Prometheus `/metrics` | `reelbolt_runner_gateway_db_pool_acquired_connections{pool}`, `reelbolt_runner_gateway_db_pool_idle_connections{pool}`, `reelbolt_runner_gateway_db_pool_max_connections{pool}` |
| Inference API | meter `ReelBolt.Inference.Database` | `reelbolt.db.server.connections` (`sum(pg_stat_database.numbackends)`, all services), `reelbolt.db.server.max_connections` |

The Go API registers its pool instruments only when OTLP export is on (`api/telemetry/dbpool.go`).
The gateway's `pool` label is `devices`, `jobs` or `switches`; the `jobs` pool's acquired count
never drops below 1 while the gateway runs, because that is the `LISTEN` connection.

The Npgsql instruments carry a `db.client.connection.pool.name` tag. Each .NET data source sets a
fixed name, so the tag never contains connection-string material such as the host, user, database
or SSL password:

| Data source | `db.client.connection.pool.name` |
|---|---|
| Inference API pool | `reelbolt-inference-api` |
| Workflow engine pool | `reelbolt-workflow-engine` |
| `BillingChangeLock` connections | `reelbolt-inference-api-billing-lock` |
| Server connection sampler | `reelbolt-inference-api-sampler` |
| Inference API startup bootstrap (target / admin database) | `reelbolt-inference-api-bootstrap` / `reelbolt-inference-api-bootstrap-admin` |
| Workflow engine startup bootstrap (target / admin database) | `reelbolt-workflow-engine-bootstrap` / `reelbolt-workflow-engine-bootstrap-admin` |

Both services create a missing database through a named bootstrap data source before migrating
(`PostgresDatabaseBootstrap` in `inference/src/ReelBolt.Shared/Data/PostgresDatabaseBootstrap.cs`), so
EF never opens its unnamed admin connection. The bootstrap probes the target database; if it answers,
migrations run. If it is missing (`3D000`), the bootstrap runs `CREATE DATABASE` on the `postgres`
database; `42P04` or `23505` on `pg_database_datname_index` (another replica or service created it
first) counts as success.

The engine bootstraps and migrates only while `Database:MigrateAtStartup` is true (its default).
With it false (env `Database__MigrateAtStartup=false`, the media host of a two-box layout) it creates
and migrates nothing, and refuses to start while a migration is pending; `--migrate-only` applies the
migrations and exits.

The bootstrap names the database the way Npgsql 10.0.3 connects. `Database` wins; without it Npgsql
sends no database and the server opens the one named after the user. That user is `Username`, else
`PGUSER`, else the Kerberos principal, else the OS user (`NpgsqlConnector.GetUsernameAsync`). Npgsql
never reads `PGDATABASE`, so the bootstrap ignores it too. When the string has neither `Database` nor
`Username`, the bootstrap probes as Npgsql connects. If that database is missing, the bootstrap reads
`session_user` on its admin connection and creates the database under that name. When Npgsql finds no
user at all, the probe fails like any other failure below.

**A bootstrap failure fails startup.** Any other failure of the probe or the create (wrong password,
unreachable server, `42501` for a role without `CREATEDB`, `57P03`, `53300`, a timeout) throws an
`InvalidOperationException` and the service exits before `MigrateAsync`, as EF's own migration would
have failed against that server. EF's unnamed admin path therefore never runs after a failed
bootstrap. The message names the step (`probe` or `create`), that step's data source, the exception
type and the SqlState when there is one, for example `Database bootstrap failed at the create step on
data source 'reelbolt-inference-api-bootstrap-admin' with PostgresException (SqlState 42501)`. It never
contains connection-string text; the original exception (whose own message may name the host or user)
is the `InnerException`. On a first boot with no database, the role in `DefaultConnection` needs
`CREATEDB`, or the database must be created beforehand.

Any new `NpgsqlDataSource` must set `Name`; without it, Npgsql uses the connection string minus the
password as the pool name.

The gateway's `/metrics` endpoint (port 8090, the pool gauges included, counts only) has no
authentication. Keep port 8090 off public networks and scrape it from the private network only.
nginx must proxy only the runner routes (`/v1/connect` and telemetry) to the gateway on 8090; never
route `/metrics` or a catch-all to it.

`PostgresConnectionSampler`
(`inference/src/ReelBolt.Inference.Api/Services/Database/PostgresConnectionSampler.cs`) queries the
server every `Database:ServerConnectionSampleSeconds` seconds, and the compose variable is
`DB_CONNECTION_SAMPLE_SECONDS`. Valid values are 1 to 86400 (default 30); 0 turns sampling off, and
any other value logs a Warning and turns it off.

The sampler counts `sum(numbackends)` from `pg_stat_database`, which any role can read, so the
Inference API's role needs no `pg_monitor` grant. The sum also counts autovacuum and parallel
workers attached to a database, up to `autovacuum_max_workers + max_parallel_workers` (about 11 at
Postgres defaults), so on `max_connections` 100 the 0.8 warning can fire at roughly 69 client
connections. This errs on the safe side: the alert can fire early, never late. The sampler opens its connection from its own one-connection data source
(`reelbolt-inference-api-sampler`), not from the API's pool, so it keeps reporting while that pool
is saturated.

After a failed sample both gauges report nothing until the next successful one, so a stale reading
never keeps an alert firing while the database is unreachable. Alert on that absence too (see
below).

## Connection alerts

The alert rules are listed with the other starting alerts in
[`observability.md`](observability.md#dashboards-to-build). In short:

- `reelbolt.db.server.connections / reelbolt.db.server.max_connections` above 0.8 for 5 minutes
  warns, above 0.9 for 2 minutes pages.
- No data from `reelbolt.db.server.connections` for 10 minutes while the Inference API is up means
  the sampler cannot reach the database or is turned off.
- Any service pool whose used connections reach its max for 2 minutes, or a rising
  `db.client.connection.npgsql.timeouts`, means that pool is exhausted.
- A steadily rising Go API `db.client.connection.wait_count` means the Go API's cap is too low for
  its load.

## Scale-out path and the PgBouncer precondition

When the pooled total no longer fits under `max_connections`, the next step is a connection pooler
in transaction mode (PgBouncer) or Npgsql multiplexing. Neither is used now. Both hand a server
connection to a different client after each transaction, so the code must not rely on anything that
outlives a transaction: no session-level advisory locks, no `LISTEN`/`NOTIFY` on a pooled
connection, and no session state (`SET`, `set_config`, temporary tables, a changed `search_path`).

Audit result for the application code (`inference/src`, `api`, `sandbox`; tests excluded), with line
numbers as of the re-run below on the `db-connection-hygiene` branch:

| Pattern | Matches | Verdict |
|---|---|---|
| `pg_advisory_lock(` | 0 | - |
| `pg_try_advisory_lock(` | 2: `inference/src/ReelBolt.Inference.Api/Services/Billing/BillingChangeLock.cs:172` and `inference/src/ReelBolt.Inference.Api/Services/Workflows/AssistantExecutionGuard.cs:154` | Blocker. Session-scoped locks. `BillingChangeLock` runs on an unpooled connection, so it must keep a direct connection or move to a transaction-scoped lock. The guard's lock is on a pooled connection; see below. |
| `pg_advisory_xact_lock(` | The call sites listed below | Safe: transaction-scoped. |
| `LISTEN` | 1: `sandbox/cmd/runner-gateway/queue.go:361` | Blocker. The gateway holds this connection for its lifetime; behind a pooler it needs a direct connection to Postgres. |
| `pg_notify` | 2: `sandbox/cmd/runner-gateway/queue.go:510`, `:532` | Safe: both run inside a transaction. |
| `SET SESSION`, `set_config`, `SET search_path` | 0 | - |
| `SET LOCAL` | 4 executing statements, all in `inference/src/ReelBolt.Inference.Api/Services/Billing/Promotions/PromotionLocks.cs` (`:63`, `:74`, `:87`, `:99`). Doc comments at `:47` and `:79` also match. | Safe: transaction-scoped. Each one runs in an open transaction (the lock helpers require one). |
| `CREATE TEMP`/`TEMPORARY TABLE` | 0 | - |
| Prepared statements | pgx's default statement cache in the Go API and the runner gateway | Needs PgBouncer 1.21 or later with `max_prepared_statements` set, or the simple query protocol. Npgsql auto-prepare is off (no `Max Auto Prepare` set). |

The `pg_advisory_xact_lock(` call sites:

- `api/handlers/internalmail/limit.go:143`
- `inference/src/ReelBolt.Inference.Api/Services/Billing/BillingEventProcessor.cs:265` and `:281`
- `inference/src/ReelBolt.Inference.Api/Data/ModelCatalogUpgrader.cs:271` (its `LockAsync`, on PostgreSQL)
- `inference/src/ReelBolt.Inference.Api/Services/Billing/NonWorkflowChargeService.cs:1182`
- `inference/src/ReelBolt.Inference.Api/Services/Billing/Promotions/PromotionLocks.cs:184`
- `inference/src/ReelBolt.Inference.Api/Services/Marketing/MailRateCounterStore.cs:178` (runs `LockSql`, declared at `:131`)
- `inference/src/ReelBolt.Shared/Billing/CreditLedger.cs:517`
- `inference/src/ReelBolt.WorkflowEngine/Services/VideoGeneration/VideoGenerationBudgetGate.cs:128`
- `sandbox/cmd/reelbolt-worker/enroll.go:149`

`AssistantExecutionGuard` is not a transaction-lock site. It takes a session-level
`pg_try_advisory_lock` on the connection its `DbContext` opened (`AssistantExecutionGuard.cs:154`). It
holds the lock across the run count, the insert and the publish, and releases it in `finally`. The
connection is taken from the pool, so the lock lives on one physical connection from take to release.
Under PgBouncer in transaction mode, or Npgsql multiplexing, the connection could change between those
steps, so the lock would stop covering them and could be stranded. Neither is used today. Keep session
pooling for the Inference API. The in-process semaphore on top of it is per replica.

Re-run the audit from the repository root:

```bash
SCOPE=(-- inference/src api sandbox ':!*_test.go' ':!**/tests/**' ':!**/*.md')
for p in 'pg_advisory_lock(' 'pg_try_advisory_lock(' 'pg_advisory_xact_lock(' 'pg_notify' \
         'SET SESSION' 'SET LOCAL' 'set_config' 'SET search_path'; do
  echo "== $p"; git grep -n -I -F -i "$p" "${SCOPE[@]}"
done
echo "== LISTEN";       git grep -n -I -F '"LISTEN ' "${SCOPE[@]}"
echo "== temp tables";  git grep -n -I -i -E 'CREATE (TEMP|TEMPORARY) TABLE' "${SCOPE[@]}"
echo "== auto-prepare"; git grep -n -I -i -E 'Max Auto Prepare|MaxAutoPrepare' "${SCOPE[@]}"
echo "== pgx exec mode"; git grep -n -I -E 'DefaultQueryExecMode|PreferSimpleProtocol|statement_cache_capacity' "${SCOPE[@]}"
```

The `pg_advisory_xact_lock(` grep also prints doc comments that mention the function; count only
the lines that execute SQL. `MailRateCounterStore` keeps its SQL in the `LockSql` constant
(`:131`, the line the grep prints) and executes it at `:178`.
