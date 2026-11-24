#!/usr/bin/env bash
# Restore drill (WP F13, docs/disaster-recovery.md).
#
# Proves, with real software, that this platform can come back:
#   * a PostgreSQL restored to a POINT IN TIME (base backup + WAL archive, recovery_target_time)
#     holds the data committed before that instant and not the data committed after it;
#   * the Data Protection key ring is inside that restored database and every provider API key
#     protected before the backup still decrypts after it;
#   * the object copy at the second provider puts a lost bucket back, byte for byte, while staying
#     clear that only user-uploaded objects are in it;
#   * the nightly backup's encrypted logical dump restores into a fresh PostgreSQL and still holds
#     the rows the assertion names - the runbook, executable.
#
# It is the same code path in every mode; only where the restored database comes from differs.
#
#   --local                 (default) build the whole thing locally with containers:
#                           PostgreSQL with archive_mode=on, a real base backup, a real WAL
#                           archive and recovery_target_time; two MinIO endpoints standing in for
#                           the primary bucket and the second provider, driven by the real
#                           infra/scripts/backup-blobs.sh and rclone. Needs Docker and nothing else
#                           - no cloud account, no credential.
#   --verify-dsn DSN        DO NOT restore anything: check a database the operator already restored
#                           from the provider's backup or snapshot (the staging drill, below).
#                           Runs the census arm: every encrypted provider key in that database must
#                           decrypt under the current key ring.
#   --pg-backup PATH        restore the nightly backup end to end: fetch it (an rclone path like
#                           remote:bucket/dumps/reelbolt-<ts>.tar.age, or a local file), decrypt it
#                           with --age-identity, take the pg_dump archive out of the tar, restore it
#                           into a throwaway PostgreSQL, print a census of counts and assert the
#                           --expect-sql row. A bare pg_dump archive, not in a tar, also works.
#                           Exclusive with --local and --verify-dsn.
#   --age-identity FILE     with --pg-backup: the age identity (AGE-SECRET-KEY-1...) the dump was
#                           encrypted to; required when the dump name ends in .age, and it stays on
#                           the operator's machine, never on the box.
#   --expect-sql "SQL"      with --pg-backup: a query whose single numeric result must be at least
#                           --expect-min (default: SELECT count(*) FROM application_users)
#   --expect-min N          with --pg-backup: the smallest acceptable result (default 1)
#   --primary REMOTE:PATH   with --verify-dsn: rclone path of the primary bucket, for the object
#   --backup REMOTE:PATH    and the second-provider copy, checked against each other instead of
#                           being rebuilt locally.
#   --keep                  keep the work directory (and print its path) for inspection. With
#                           --pg-backup this directory holds the DECRYPTED plaintext dump - every
#                           customer email and provider key - so delete it once you are done.
#   -h | --help             this text
#
# Settings: DRILL_PG_IMAGE, DRILL_MINIO_IMAGE, DRILL_MC_IMAGE, DRILL_RCLONE_IMAGE, DRILL_ALPINE_IMAGE
# override the images; DRILL_DOTNET overrides the dotnet executable (default dotnet, which must be
# on PATH - that is what runs the two gated facts in RestoreDrillTests). DRILL_AGE_BIN and
# DRILL_RCLONE_BIN override the age and rclone commands (default age and rclone); both are command
# prefixes, so a container is spelled "docker run --rm -i <image> age".
#
# The staging drill, which is the one F13's acceptance names and which needs a real account:
#   1. In the provider's console take a point-in-time restore (Scaleway: a snapshot or a backup,
#      restored to a new instance) or, on a PostgreSQL we run ourselves, restore the base backup
#      with the WAL archive into a new instance. Never restore in place.
#   2. Point this script at the restored instance:
#        infra/scripts/restore-drill.sh --verify-dsn "<restored connection string>" \
#          --primary primary:<bucket> --backup reelbolt_backup:<bucket>
#      The connection string needs the provider's TLS settings, and the environment needs whatever
#      the KMS wrapping needs - the same DATA_PROTECTION_KMS_KEY_ID / AWS_* pair as /opt/reelbolt/.env
#      - or every key is expectedly unreadable.
#   3. It exits 0 only when the key ring is present and every provider key decrypts.
# docs/disaster-recovery.md has the runbook this implements, the RPO/RTO targets and what the
# local stand-ins do and do not prove.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
test_project="$repo_root/inference/tests/ReelBolt.WorkflowEngine.Tests/ReelBolt.WorkflowEngine.Tests.csproj"
backup_script="$script_dir/backup-blobs.sh"

pg_image="${DRILL_PG_IMAGE:-postgres:16-alpine}"
minio_image="${DRILL_MINIO_IMAGE:-quay.io/minio/minio}"
mc_image="${DRILL_MC_IMAGE:-quay.io/minio/mc}"
rclone_image="${DRILL_RCLONE_IMAGE:-rclone/rclone}"
alpine_image="${DRILL_ALPINE_IMAGE:-alpine:3.21}"
dotnet_bin="${DRILL_DOTNET:-dotnet}"

# Throwaway credentials for the drill's own containers. MinIO refuses a root password shorter
# than eight characters, which is the only reason this one is long; it never leaves localhost,
# and the secret tripwire's marker on the line is what keeps infra/scripts/check-secrets.sh quiet.
pg_user=drill
pg_password=drill-only-local # secret-scan: allow
pg_database=reelforge_drill

# The age and rclone commands are prefixes, not paths - a container is a prefix - split into arrays
# exactly as backup-blobs.sh splits RCLONE_BIN.
age_bin="${DRILL_AGE_BIN:-age}"
# shellcheck disable=SC2206 # age_bin is deliberately a command prefix ("age", or a container invocation).
readonly -a age_cmd=($age_bin)
rclone_bin="${DRILL_RCLONE_BIN:-rclone}"
# shellcheck disable=SC2206 # rclone_bin is deliberately a command prefix ("rclone", or a container invocation).
readonly -a rclone_cmd=($rclone_bin)
readonly rclone_config="${RCLONE_CONFIG:-/opt/reelbolt/rclone.conf}"

mode=local
mode_flag=""
keep=false
verify_dsn=""
primary_remote=""
backup_remote=""
pg_backup_path=""
age_identity=""
expect_sql="SELECT count(*) FROM application_users"
expect_min=1

usage() { sed -n '2,64p' "$0" | sed 's/^# \{0,1\}//'; }

# The three modes are mutually exclusive: --pg-backup is not a modifier of --local or --verify-dsn.
select_mode() {
  local flag="$1" m="$2"
  if [[ -n "$mode_flag" && "$mode_flag" != "$flag" ]]; then
    echo "restore-drill: --$mode_flag and --$flag are mutually exclusive" >&2
    usage >&2
    exit 2
  fi
  mode="$m"
  mode_flag="$flag"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --local) select_mode local local ;;
    --verify-dsn) select_mode verify-dsn verify; verify_dsn="${2:?--verify-dsn needs a connection string}"; shift ;;
    --pg-backup) select_mode pg-backup pgdump; pg_backup_path="${2:?--pg-backup needs a path}"; shift ;;
    --age-identity) age_identity="${2:?--age-identity needs a file}"; shift ;;
    --expect-sql) expect_sql="${2:?--expect-sql needs a query}"; shift ;;
    --expect-min) expect_min="${2:?--expect-min needs a number}"; shift ;;
    --primary) primary_remote="${2:?--primary needs an rclone path}"; shift ;;
    --backup) backup_remote="${2:?--backup needs an rclone path}"; shift ;;
    --keep) keep=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "restore-drill: unknown argument $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

if [[ "$mode" == "verify" && -z "$verify_dsn" ]]; then
  echo "restore-drill: --verify-dsn needs a connection string" >&2
  exit 2
fi

if [[ "$mode" == "pgdump" ]]; then
  if ! [[ "$expect_min" =~ ^[0-9]+$ ]]; then
    echo "restore-drill: --expect-min must be a non-negative integer (got '$expect_min')" >&2
    exit 2
  fi
  if [[ -z "$expect_sql" ]]; then
    echo "restore-drill: --expect-sql must not be empty" >&2
    exit 2
  fi
  if [[ -n "$primary_remote" || -n "$backup_remote" ]]; then
    echo "restore-drill: --primary/--backup only apply to --verify-dsn" >&2
    exit 2
  fi
fi

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
ok() { printf '   PASS  %s\n' "$*"; }
note() { printf '   NOTE  %s\n' "$*"; }
fail() { printf '   FAIL  %s\n' "$*" >&2; exit 1; }

run_id="${DRILL_RUN_ID:-$(date -u +%Y%m%d%H%M%S)-$$}"
net="rbdrill-${run_id}"
work="$(mktemp -d "${TMPDIR:-/tmp}/reelbolt-restore-drill.XXXXXX")"
state_file="$work/drill-state.json"
verify_state="$work/verify-state.json"
checks=0

src_container="rbdrill-src-${run_id}"
restored_container="rbdrill-restored-${run_id}"
primary_container="rbdrill-primary-${run_id}"
secondary_container="rbdrill-secondary-${run_id}"
src_volume="rbdrill-srcdata-${run_id}"
wal_volume="rbdrill-wal-${run_id}"
base_volume="rbdrill-base-${run_id}"

cleanup() {
  docker rm -f "$src_container" "$restored_container" "$primary_container" "$secondary_container" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  docker volume rm "$src_volume" "$wal_volume" "$base_volume" >/dev/null 2>&1 || true
  if [[ "$keep" == "true" ]]; then
    say "restore-drill: work directory kept at $work"
  else
    rm -rf "$work"
  fi
}

# verify_existing restores nothing and owns no container, so only the restoring modes clean up.
if [[ "$mode" != "verify" ]]; then
  trap cleanup EXIT
fi

need_images() {
  local images=("$@")
  if [[ "${#images[@]}" -eq 0 ]]; then
    images=("$pg_image" "$minio_image" "$mc_image" "$rclone_image" "$alpine_image")
  fi
  local image
  for image in "${images[@]}"; do
    if ! docker image inspect "$image" >/dev/null 2>&1; then
      say "restore-drill: pulling $image"
      docker pull "$image" >/dev/null
    fi
  done
}

start_minio() {
  local name="$1"
  docker run -d --name "$name" --network "$net" --tmpfs /data \
    -e MINIO_ROOT_USER="$pg_user" -e MINIO_ROOT_PASSWORD="$pg_password" \
    "$minio_image" server /data --address ":9000" >/dev/null
}

wait_for_minio() {
  local name="$1"
  for _ in $(seq 1 60); do
    if docker run --rm --network "$net" "$alpine_image" wget -qO- "http://$name:9000/minio/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  docker logs "$name" 2>&1 | tail -20
  fail "$name never became ready"
}

mc_run() {
  docker run --rm --network "$net" \
    -e "MC_HOST_primary=http://$pg_user:$pg_password@$primary_container:9000" \
    -e "MC_HOST_secondary=http://$pg_user:$pg_password@$secondary_container:9000" \
    -v "$work/seed:/seed:ro" \
    "$mc_image" "$@"
}

psql_src() { docker exec -i "$src_container" psql -U "$pg_user" -d "$pg_database" -v ON_ERROR_STOP=1 -tAc "$1"; }
psql_restored() { docker exec -i "$restored_container" psql -U "$pg_user" -d "$pg_database" -v ON_ERROR_STOP=1 -tAc "$1"; }

# The image starts a TEMPORARY server (unix socket only, on the `postgres` database) to run initdb
# and the init scripts, stops it, and only then starts the real one. A wait on `postgres` can be
# satisfied by that temporary server, which then disappears mid-sequence ("connection to server on
# socket ... No such file or directory"), so wait for the entrypoint's own "init process complete"
# line first: it is printed after the temporary server is stopped, so a wait that follows it can
# only ever see the real server.
wait_for_pg_init() {
  local container="$1"
  for _ in $(seq 1 90); do
    if docker logs "$container" 2>&1 | grep -q 'PostgreSQL init process complete'; then
      return 0
    fi
    sleep 1
  done
  docker logs "$container" 2>&1 | tail -30
  fail "$container never reported that its init process completed"
}

wait_for_pg() {
  local container="$1" user="${2:-$pg_user}" db="${3:-$pg_database}"
  # pg_isready answers as soon as the image's TEMPORARY server is up, which is before the image has
  # created POSTGRES_DB. A query against the drill's own database is what proves the cluster the
  # drill will actually use is the one that is ready; without it a restore can race the init and
  # fail with "database ... does not exist". The user and database default to the drill's own; the
  # pgdump arm calls wait_for_pg_init first, then waits on the image's postgres superuser before the
  # drill role exists.
  for _ in $(seq 1 90); do
    if docker exec "$container" psql -U "$user" -d "$db" -tAc 'SELECT 1' >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  docker logs "$container" 2>&1 | tail -30
  fail "$container never accepted connections"
}

# Archive recovery serves read-only connections while it is still replaying, so pg_isready alone
# would let the checks below run against a half-restored cluster. Wait for the promotion too.
wait_for_promotion() {
  local container="$1"
  for _ in $(seq 1 90); do
    if [[ "$(docker exec -i "$container" psql -U "$pg_user" -d "$pg_database" -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null)" == "f" ]]; then
      return 0
    fi
    sleep 1
  done
  docker logs "$container" 2>&1 | tail -30
  fail "$container never finished recovery"
}

host_port() { docker port "$1" 5432/tcp | head -1 | sed 's/.*://'; }

drill_dsn() { printf 'Host=127.0.0.1;Port=%s;Database=%s;Username=%s;Password=%s' "$1" "$pg_database" "$pg_user" "$pg_password"; }

run_fact() {
  local filter="$1" output="$2"
  shift 2
  # The facts return immediately without their environment, so the drill always sets it and always
  # checks the artefact they write: a silently skipped fact leaves no artefact and fails the drill.
  if ! "$dotnet_bin" test "$test_project" --nologo --filter "FullyQualifiedName~$filter" -v q "$@" >"$output" 2>&1; then
    tail -40 "$output"
    fail "$filter failed"
  fi
}

state_flag() {
  grep -Eq '"verifyOk"[[:space:]]*:[[:space:]]*true' "$1"
}

# ---------------------------------------------------------------- object store half

blob_drill() {
  step "object copy: primary -> second provider -> back (two real S3 endpoints, real rclone)"
  if [[ -z "$primary_remote" || -z "$backup_remote" ]]; then
    local project_id job_id
    project_id="$(uuidgen 2>/dev/null || echo "11111111-1111-4111-8111-111111111111")"
    job_id="$(uuidgen 2>/dev/null || echo "22222222-2222-4222-8222-222222222222")"
    mkdir -p "$work/seed/projects/$project_id/userFiles" "$work/seed/projects/$project_id/outputFiles" "$work/seed/runner-staging/$job_id"
    head -c 262144 /dev/urandom >"$work/seed/projects/$project_id/userFiles/interview footage.mp4"
    printf 'drill notes\n' >"$work/seed/projects/$project_id/userFiles/notes.txt"
    head -c 131072 /dev/urandom >"$work/seed/projects/$project_id/outputFiles/render.mp4"
    printf 'staged upload\n' >"$work/seed/runner-staging/$job_id/0"

    start_minio "$primary_container"
    start_minio "$secondary_container"
    wait_for_minio "$primary_container"
    wait_for_minio "$secondary_container"
    mc_run mb --ignore-existing "primary/drill" >/dev/null
    mc_run mb --ignore-existing "secondary/drill" >/dev/null
    mc_run mirror --overwrite /seed "primary/drill" >/dev/null
    ok "primary bucket seeded with 2 userFiles objects, 1 outputFiles object and 1 runner-staging object"

    # Two rclone remotes over two real S3 endpoints. rclone itself runs from a container, so the
    # production script is exercised exactly as it is written, not reimplemented here.
    cat >"$work/rclone.conf" <<RCLONE
[primary]
type = s3
provider = Minio
endpoint = http://rbdrill-primary:9000
access_key_id = $pg_user
secret_access_key = $pg_password
region = us-east-1
force_path_style = true

[secondary]
type = s3
provider = Minio
endpoint = http://rbdrill-secondary:9000
access_key_id = $pg_user
secret_access_key = $pg_password
region = us-east-1
force_path_style = true
RCLONE
    # The container names carry the run id, so rewrite the two endpoints to this run's names.
    sed -i "s/rbdrill-primary/$primary_container/; s/rbdrill-secondary/$secondary_container/" "$work/rclone.conf"

    export RCLONE_BIN="docker run --rm --network $net -v $work:/work:ro -v $work/logs:/logs $rclone_image"
    export RCLONE_CONFIG=/work/rclone.conf
    # rclone writes its log inside the container; the mount above lands it in the same host file.
    export BLOB_BACKUP_RCLONE_LOG=/logs/backup-blobs.log
    # rclone addresses a remote as remote:path; mc (above) addresses a bucket as alias/bucket.
    export BLOB_BACKUP_SOURCE="primary:drill"
    export BLOB_BACKUP_TARGET="secondary:drill"
    export BLOB_BACKUP_LOG_DIR="$work/logs"
  else
    export BLOB_BACKUP_SOURCE="$primary_remote"
    export BLOB_BACKUP_TARGET="$backup_remote"
    export BLOB_BACKUP_LOG_DIR="$work/logs"
    mkdir -p "$work/logs"
  fi

  if ! "$backup_script"; then
    tail -30 "$BLOB_BACKUP_LOG_DIR/backup-blobs.log" 2>/dev/null || true
    fail "the nightly backup script failed"
  fi
  ok "backup-blobs.sh copied the userFiles objects to the second provider and verified count and bytes"
  checks=$((checks + 1))

  if [[ -z "$primary_remote" ]]; then
    # The disaster: the primary bucket is gone. Only what the second provider holds can come back.
    mc_run rm --recursive --force "primary/drill/" >/dev/null
    mc_run mb --ignore-existing "primary/drill" >/dev/null
    ok "primary bucket emptied (the disaster this half exists for)"

    "$backup_script" --restore || fail "restoring from the second provider failed"
    ok "backup-blobs.sh --restore put the copy back"

    local key hash restored_hash failures=0
    while IFS='|' read -r key hash; do
      restored_hash="$(mc_run cat "primary/drill/$key" | sha256sum | cut -d' ' -f1)"
      if [[ "$restored_hash" == "$hash" ]]; then
        ok "restored byte-for-byte: $key"
        checks=$((checks + 1))
      else
        printf '   FAIL  %s restored with a different hash\n' "$key" >&2
        failures=$((failures + 1))
      fi
    done < <(find "$work/seed" -type f -path '*/userFiles/*' -printf '%P|' -exec sha256sum {} \; | awk -F'|' '{split($2,a," "); print $1"|"a[1]}')
    [[ "$failures" -eq 0 ]] || fail "$failures restored object(s) did not match"

    for absent in "projects/$project_id/outputFiles/render.mp4" "runner-staging/$job_id/0"; do
      if mc_run stat "primary/drill/$absent" >/dev/null 2>&1; then
        fail "$absent is in the primary bucket after the restore; it is deliberately not backed up"
      fi
    done
    ok "outputFiles and runner-staging are absent after the restore: they are regenerable and deliberately not backed up"
  fi
}

# ---------------------------------------------------------------- database half

database_drill() {
  step "database: base backup + WAL archive, restored to a point in time"
  docker volume create "$src_volume" >/dev/null
  docker volume create "$wal_volume" >/dev/null
  docker volume create "$base_volume" >/dev/null
  # The volumes come up root-owned and only PostgreSQL may write them. The image's own postgres
  # user is asked for its ids rather than assuming 999: the alpine images use 70.
  pg_uid="$(docker run --rm "$pg_image" id -u postgres)"
  pg_gid="$(docker run --rm "$pg_image" id -g postgres)"
  docker run --rm -v "$wal_volume:/w" "$alpine_image" chown "$pg_uid:$pg_gid" /w
  docker run --rm -v "$base_volume:/w" "$alpine_image" chown "$pg_uid:$pg_gid" /w

  docker run -d --name "$src_container" --network "$net" -p 127.0.0.1::5432 \
    -e POSTGRES_USER="$pg_user" -e POSTGRES_PASSWORD="$pg_password" -e POSTGRES_DB="$pg_database" \
    -v "$src_volume:/var/lib/postgresql/data" -v "$wal_volume:/wal" -v "$base_volume:/base" \
    "$pg_image" \
    -c wal_level=replica -c archive_mode=on -c archive_timeout=60 \
    -c "archive_command=test ! -f /wal/%f && cp %p /wal/%f" >/dev/null
  wait_for_pg "$src_container"
  local src_port src_dsn
  src_port="$(host_port "$src_container")"
  src_dsn="$(drill_dsn "$src_port")"
  ok "source PostgreSQL on 127.0.0.1:$src_port with archive_mode=on"

  REELBOLT_DRILL_MODE=seeded REELBOLT_DRILL_SEED_DSN="$src_dsn" REELBOLT_DRILL_STATE="$state_file" \
    run_fact "Seed_writes_the_key_ring" "$work/seed-test.log"
  ok "migrations applied; key ring written to data_protection_keys; one provider key protected"
  checks=$((checks + 1))

  # The marker table is created BEFORE the base backup, so the table itself travels in the base
  # backup and its rows arrive afterwards through WAL. Each half of the restore is then provable.
  psql_src 'CREATE TABLE drill_markers (seq int PRIMARY KEY, note text NOT NULL, at timestamptz NOT NULL DEFAULT clock_timestamp())' >/dev/null

  # -u postgres and not root: the restored cluster runs as uid 999 and has to read what this writes.
  docker exec -i -u postgres -e PGPASSWORD="$pg_password" "$src_container" \
    pg_basebackup -U "$pg_user" -h 127.0.0.1 -D /base -Fp -X stream -c fast >/dev/null
  ok "base backup taken while the cluster was running"

  psql_src "INSERT INTO drill_markers (seq, note) VALUES (1, 'wal-replay')" >/dev/null
  psql_src "UPDATE inference_providers SET model_name = 'wal-replayed' WHERE name LIKE 'drill-restore-%'" >/dev/null
  psql_src 'SELECT pg_switch_wal()' >/dev/null
  sleep 2
  local target_time
  target_time="$(psql_src "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') || '+00'")"
  sleep 2
  psql_src "INSERT INTO drill_markers (seq, note) VALUES (2, 'after-target')" >/dev/null
  psql_src 'SELECT pg_switch_wal()' >/dev/null
  ok "committed a WAL-only change, then switched WAL, then committed one more after the target time $target_time"

  docker run --rm -v "$base_volume:/data" "$alpine_image" \
    sh -c "touch /data/recovery.signal && chown $pg_uid:$pg_gid /data/recovery.signal"
  docker run -d --name "$restored_container" --network "$net" -p 127.0.0.1::5432 \
    -v "$base_volume:/var/lib/postgresql/data" -v "$wal_volume:/wal" \
    "$pg_image" \
    -c "restore_command=test -f /wal/%f && cp /wal/%f %p" \
    -c "recovery_target_time=$target_time" \
    -c recovery_target_action=promote >/dev/null
  wait_for_pg "$restored_container"
  wait_for_promotion "$restored_container"
  ok "restored cluster promoted out of archive recovery"

  local markers after model
  markers="$(psql_restored "SELECT count(*) FROM drill_markers WHERE note = 'wal-replay'")"
  after="$(psql_restored "SELECT count(*) FROM drill_markers WHERE note = 'after-target'")"
  model="$(psql_restored "SELECT model_name FROM inference_providers WHERE name LIKE 'drill-restore-%' LIMIT 1")"
  [[ "$markers" == "1" ]] || fail "the row committed before the target time is missing from the restore (got '$markers')"
  ok "row committed before the target time is present (replayed from WAL)"
  checks=$((checks + 1))
  [[ "$after" == "0" ]] || fail "the row committed after the target time is in the restore (got '$after'); the point in time was not honoured"
  ok "row committed after the target time is absent: recovery stopped at the target, not at the end of the WAL"
  checks=$((checks + 1))
  [[ "$model" == "wal-replayed" ]] || fail "the provider row updated after the base backup reads '$model'"
  ok "an application table changed after the base backup carries the post-backup value"
  checks=$((checks + 1))

  # A fresh state file with no verifyOk in it: the flag below can only have been written by this run.
  cp "$state_file" "$verify_state"
  REELBOLT_DRILL_MODE=seeded REELBOLT_DRILL_VERIFY_DSN="$(drill_dsn "$(host_port "$restored_container")")" \
    REELBOLT_DRILL_STATE="$verify_state" \
    run_fact "Verify_restored_database_still_decrypts" "$work/verify-test.log"
  state_flag "$verify_state" || fail "the verify fact left no verifyOk flag; the drill tested nothing"
  ok "the restored key ring decrypts the provider key protected before the backup, and refuses a wrong wrapper"
  checks=$((checks + 1))
}

# ---------------------------------------------------------------- logical dump half

# Counts only: the census is built so that nothing it prints can be an email, a key or a row.
restored_count() {
  local table="$1" present
  present="$(psql_restored "SELECT to_regclass('public.$table') IS NOT NULL")"
  if [[ "$present" == "t" ]]; then
    psql_restored "SELECT count(*) FROM public.$table"
  else
    printf 'absent'
  fi
}

pg_dump_drill() {
  step "logical dump: fetch, decrypt, restore into a throwaway PostgreSQL, assert a known row"
  local source_name="${pg_backup_path##*/}"
  local fetched="$work/restore.dump.age"
  local payload="$work/restore.payload"
  local dump="$work/restore.dump"
  local ring_files=0

  # Fetch: an rclone remote is downloaded, anything else is a local file copied into the work dir.
  if [[ "$pg_backup_path" =~ ^[A-Za-z0-9_.-]+: ]]; then
    command -v "${rclone_cmd[0]}" >/dev/null 2>&1 \
      || fail "$pg_backup_path is an rclone path but ${rclone_cmd[0]} is not on PATH"
    say "restore-drill: fetching $pg_backup_path with ${rclone_cmd[0]}"
    "${rclone_cmd[@]}" copyto "$pg_backup_path" "$fetched" --config "$rclone_config" \
      || fail "rclone could not fetch $pg_backup_path"
  else
    [[ -f "$pg_backup_path" ]] || fail "no such dump file: $pg_backup_path"
    cp -- "$pg_backup_path" "$fetched" || fail "could not copy $pg_backup_path"
  fi
  [[ -s "$fetched" ]] || fail "the fetched object is empty: $pg_backup_path"
  ok "fetched $source_name ($(stat -c %s "$fetched") bytes)"
  checks=$((checks + 1))

  # Decrypt. A name ending in .age is an age v1 file and needs the identity; anything else is taken
  # as already plain. A wrong or missing identity stops the drill here, by design: this is the step
  # that proves the documented procedure is the one that actually restores.
  if [[ "$source_name" == *.age ]]; then
    [[ -n "$age_identity" ]] || fail "$source_name is an age file; pass --age-identity"
    [[ -f "$age_identity" ]] || fail "no such age identity file: $age_identity"
    command -v "${age_cmd[0]}" >/dev/null 2>&1 \
      || fail "${age_cmd[0]} is not on PATH; it is needed to decrypt $source_name"
    say "restore-drill: decrypting $source_name with ${age_cmd[0]}"
    if ! "${age_cmd[@]}" -d -i "$age_identity" <"$fetched" >"$payload" 2>"$work/age.err"; then
      rm -f "$payload"
      tail -5 "$work/age.err" >&2 || true
      fail "age could not decrypt $source_name (wrong or missing identity?)"
    fi
  else
    cp -- "$fetched" "$payload"
  fi
  if [[ ! -s "$payload" ]]; then
    rm -f "$payload"
    fail "the payload is empty: $source_name"
  fi

  # The frozen v1 object is an uncompressed POSIX tar holding reelforge.dump plus the Data
  # Protection ring under keys/. A bare pg_dump archive is still accepted, so an operator can point
  # the drill at an extracted dump. tar -tf both classifies the payload and lists its members; the
  # ring is counted by name only, never read.
  if tar -tf "$payload" >"$work/tar.list" 2>/dev/null; then
    grep -qxF 'reelforge.dump' "$work/tar.list" \
      || fail "the archive has no reelforge.dump member: $source_name"
    ring_files="$(grep -c '^keys/.' "$work/tar.list" || true)"
    mkdir -p "$work/extract"
    tar -xf "$payload" -C "$work/extract" reelforge.dump \
      || fail "could not extract reelforge.dump from the archive: $source_name"
    [[ -f "$work/extract/reelforge.dump" && ! -L "$work/extract/reelforge.dump" ]] \
      || fail "the reelforge.dump member is not a regular file: $source_name"
    mv -- "$work/extract/reelforge.dump" "$dump"
    ok "the archive holds reelforge.dump and $ring_files key ring file(s)"
  else
    cp -- "$payload" "$dump"
  fi
  if [[ ! -s "$dump" ]]; then
    rm -f "$dump"
    fail "the dump is empty: $source_name"
  fi
  if [[ "$(head -c 5 "$dump")" != "PGDMP" ]]; then
    rm -f "$dump"
    fail "the dump is not a pg_dump archive (no PGDMP magic): $source_name"
  fi
  ok "the dump is a pg_dump -Fc archive ($(stat -c %s "$dump") bytes)"
  checks=$((checks + 1))

  # Restore. The cluster is initialised with POSTGRES_USER=postgres, because initdb needs a
  # superuser, but the restore itself runs as a NOSUPERUSER role that owns only the drill database:
  # a hostile archive cannot use COPY ... FROM PROGRAM or any other server-side program, which needs
  # superuser, and it owns nothing but the throwaway database. The credentials are the drill's own,
  # never the operator's, and the target is only ever a container.
  step "restore into a throwaway PostgreSQL (a non-superuser drill role, never the operator's)"
  docker run -d --name "$restored_container" --network "$net" \
    -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD="$pg_password" -e POSTGRES_DB=postgres \
    "$pg_image" >/dev/null
  wait_for_pg_init "$restored_container"
  wait_for_pg "$restored_container" postgres postgres
  ok "throwaway PostgreSQL up (the image's initdb superuser is postgres)"
  docker exec "$restored_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE ROLE \"$pg_user\" NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD '$pg_password'" >/dev/null
  docker exec "$restored_container" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"$pg_database\" OWNER \"$pg_user\"" >/dev/null
  wait_for_pg "$restored_container" "$pg_user" "$pg_database"
  ok "created the NOSUPERUSER role $pg_user owning $pg_database and waited for it"
  if ! docker exec -i "$restored_container" pg_restore -U "$pg_user" -d "$pg_database" \
      --no-owner --no-privileges --exit-on-error <"$dump"; then
    fail "pg_restore did not restore the dump cleanly"
  fi
  ok "pg_restore --no-owner --no-privileges --exit-on-error restored the archive as $pg_user"
  checks=$((checks + 1))

  # Census: counts only, so no email, key or row can reach the log.
  step "census (counts only)"
  local line="census:" t
  for t in application_users organizations projects; do
    line+=" $t=$(restored_count "$t")"
  done
  if [[ "$(psql_restored "SELECT to_regclass('public.data_protection_keys') IS NOT NULL")" == "t" ]]; then
    line+=" data_protection_keys=$(psql_restored 'SELECT count(*) FROM public.data_protection_keys')"
  fi
  line+=" public_tables=$(psql_restored "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")"
  line+=" key_ring_files=$ring_files"
  note "$line"
  checks=$((checks + 1))

  # Assert: the known row the runbook promises is there. The result must be a single number, so a
  # query that returns a row, several rows or an error cannot masquerade as a passing count.
  step "assert a known row"
  local got
  if ! got="$(psql_restored "$expect_sql")"; then
    fail "--expect-sql could not be run against the restored database (query: $expect_sql)"
  fi
  if ! [[ "$got" =~ ^[0-9]+$ ]]; then
    fail "--expect-sql did not return a single number (query: $expect_sql)"
  fi
  if (( got < expect_min )); then
    fail "--expect-sql returned $got, below --expect-min $expect_min (query: $expect_sql)"
  fi
  ok "--expect-sql returned $got, at or above --expect-min $expect_min (query: $expect_sql)"
  checks=$((checks + 1))
}

verify_existing() {
  step "verify a database restored outside this script"
  REELBOLT_DRILL_MODE=census REELBOLT_DRILL_VERIFY_DSN="$verify_dsn" REELBOLT_DRILL_STATE="$verify_state" \
    run_fact "Verify_restored_database_still_decrypts" "$work/verify-test.log"
  state_flag "$verify_state" || fail "the verify fact left no verifyOk flag; the drill tested nothing"
  ok "$(sed -n 's/.*"verifyDetail": "\([^"]*\)".*/\1/p' "$verify_state" | head -1)"
  checks=$((checks + 1))

  if [[ -n "$primary_remote" && -n "$backup_remote" ]]; then
    step "compare the object copy against the primary bucket"
    # One-way by size: everything the primary holds must be in the copy. A difference is reported
    # and does NOT fail the drill, because the copy is nightly by design - a file uploaded after
    # last night's run is legitimately absent from it, and that lag is the blob RPO.
    if ! command -v rclone >/dev/null 2>&1; then
      note "rclone is not on PATH, so the bucket comparison did not run; install it, or use --local, which runs the copy inside a container"
    elif rclone check "$primary_remote" "$backup_remote" --include "/projects/*/userFiles/**" --one-way --size-only; then
      ok "the second-provider copy holds every userFiles object the primary holds"
      checks=$((checks + 1))
    else
      note "the copy is behind the primary (expected between nightly runs); re-run tonight's backup before treating it as evidence"
    fi
  fi
}

if [[ "$mode" == "pgdump" ]]; then
  # The dump arm needs only the server image; rclone is whatever DRILL_RCLONE_BIN names (an
  # installed binary, or a container that pulls its own image on first use).
  need_images "$pg_image"
else
  need_images
fi
say "restore-drill: run $run_id, work directory $work"

if [[ "$mode" == "pgdump" ]]; then
  docker network create "$net" >/dev/null
  pg_dump_drill
elif [[ "$mode" == "local" ]]; then
  docker network create "$net" >/dev/null
  blob_drill
  database_drill
else
  verify_existing
fi

step "drill complete: $checks check(s) passed"
say "RPO/RTO and what these stand-ins do not prove: docs/disaster-recovery.md"
