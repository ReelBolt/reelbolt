#!/usr/bin/env bash
# End-to-end test of the encrypted Postgres backup chain (docs/postgres-backups.md).
#
# Proves, hermetically and with the REAL scripts, that a seeded database survives the whole
# chain: infra/scripts/backup-postgres.sh dumps the database out of a container, packs it (and,
# when PG_BACKUP_INCLUDE_RING=true, the Data Protection ring) into one uncompressed tar, encrypts
# that tar with age and uploads it to a real S3 endpoint, verifying the object the backend stored.
# The ring is opt-in: by default the archive holds reelforge.dump only, and a run that requests the
# ring with none present refuses (RING_REQUIRED) and uploads nothing. The ring archive is then
# downloaded again and infra/scripts/restore-drill.sh --pg-backup decrypts the tar, restores the
# dump into a throwaway PostgreSQL as a non-superuser role and asserts the seeded row. Four cases
# prove a run can fail: a byte-corrupted ciphertext, a requested-but-absent ring, an archive whose
# tar has no reelforge.dump member, and (F-QA-4) a perfectly valid dump whose asserted row is
# absent, each make a run exit non-zero.
#
# This is NOT a local-gate step: it needs Docker and image pulls, exactly like
# infra/scripts/restore-drill.sh, which is run on demand for the same reason. shellcheck still
# covers it, because it globs infra/scripts/**/*.sh.
#
# The S3 endpoint is `rclone serve s3` (a real S3 server with SigV4), not MinIO, because the MinIO
# images cannot be pulled in this environment; the rclone client and both production scripts are
# the real ones. The age image is built (alpine + `apk add age`), not pulled - there is no
# reliable public age image - by committing a container, so the test needs no build-time network.
# The upload verification compares the object's size and the hash the backend reports (rclone's
# md5 ETag, which `rclone serve s3` does report); it is deliberately not a sha256 comparison,
# because `rclone hashsum sha256` does not work against S3.
#
# What it does not prove: the real Cloudflare R2 bucket, the lifecycle rule and the systemd timer
# (T-004, T-005); those are operator steps on the box. It also proves nothing about the media box,
# which holds no database by design.
#
#   infra/scripts/tests/backup-postgres.test.sh
set -euo pipefail

tests_dir="$(cd "$(dirname "$0")" && pwd)"
scripts_dir="$(cd "$tests_dir/.." && pwd)"
backup_script="$scripts_dir/backup-postgres.sh"
drill_script="$scripts_dir/restore-drill.sh"

for tool in docker jq; do
  command -v "$tool" >/dev/null 2>&1 || { echo "error: missing $tool" >&2; exit 2; }
done
docker info >/dev/null 2>&1 || { echo "error: the docker daemon is not reachable" >&2; exit 2; }

pg_image="${DRILL_PG_IMAGE:-postgres:16-alpine}"
rclone_image="${DRILL_RCLONE_IMAGE:-rclone/rclone}"
alpine_image="${DRILL_ALPINE_IMAGE:-alpine:3.21}"

# Throwaway credentials for this test's own containers. They never leave localhost; the marker on
# the password line is what keeps infra/scripts/check-secrets.sh quiet, as restore-drill.sh does.
pg_user=drill
pg_password=drill-only-local # secret-scan: allow
pg_database=reelforge_drill

run_id="$(date -u +%Y%m%d%H%M%S)-$$"
net="rbbackup-${run_id}"
age_image="reelbolt-backup-test-age:${run_id}"
src_container="rbbackup-src-${run_id}"
s3_container="rbbackup-s3-${run_id}"
age_build="rbbackup-age-${run_id}"

work="$(mktemp -d "${TMPDIR:-/tmp}/reelbolt-backup-test.XXXXXX")"
backup_work="$work/backup"     # PG_BACKUP_WORK_DIR: the PARENT the script makes its mktemp work dir in
log_dir="$work/logs"           # PG_BACKUP_LOG_DIR
s3_root="$work/s3"             # the rclone serve s3 backing directory (the bucket root on disk)
restored_dir="$work/restored"
keys_dir="$work/keys"          # PG_BACKUP_KEYS_DIR: the seeded Data Protection ring (one key-*.xml)
empty_keys_dir="$work/no-keys" # PG_BACKUP_KEYS_DIR for the "ring requested but absent" case (empty)
# One bucket per ring case, so each case's dumps/ prefix holds exactly the objects that case wrote.
default_bucket="reelbolt-backups" # default: no ring requested
ring_bucket="reelbolt-ring"       # PG_BACKUP_INCLUDE_RING=true with a seeded ring
noring_bucket="reelbolt-noring"   # PG_BACKUP_INCLUDE_RING=true with no ring: must stay empty
mkdir -p "$backup_work" "$log_dir" "$s3_root" "$restored_dir" "$keys_dir" "$empty_keys_dir"

# One ring file, named so the script's key-*.xml glob picks it up. Its content is never read: the
# test asserts only that the archive carries it, and the drill counts it by name.
printf '<key id="test" />\n' >"$keys_dir/key-test.xml"

cleanup() {
  docker rm -f "$src_container" "$s3_container" "$age_build" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  docker image rm "$age_image" >/dev/null 2>&1 || true
  # The containers write into the work directory as root; take it back before removing the tree.
  docker run --rm -v "$work:/w" "$alpine_image" chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

passed=0
failed=0
ok() { printf '  ok   %s\n' "$1"; passed=$((passed + 1)); }
ko() {
  printf '  FAIL %s\n' "$1"
  if [ $# -gt 1 ]; then printf '%s\n' "$2" | sed 's/^/       /'; fi
  failed=$((failed + 1))
}
step() { printf '\n== %s\n' "$1"; }

assert_contains() { # NAME HAYSTACK NEEDLE
  if printf '%s' "$2" | grep -qF -- "$3"; then
    ok "$1"
  else
    ko "$1 (missing: $3)" "$2"
  fi
}

assert_not_contains() { # NAME HAYSTACK NEEDLE
  if printf '%s' "$2" | grep -qF -- "$3"; then
    ko "$1 (unexpectedly present: $3)" "$2"
  else
    ok "$1"
  fi
}

need_image() {
  docker image inspect "$1" >/dev/null 2>&1 && return 0
  printf 'backup-postgres-test: pulling %s\n' "$1" >&2
  docker pull "$1" >/dev/null
}

# rclone_ctl runs the real rclone image against the test's S3 endpoint, the same command prefix the
# backup script is given, so the bucket is addressed exactly as the script addresses it.
rclone_ctl() {
  docker run --rm --network "$net" -v "$work:$work" "$rclone_image" "$@" --config "$work/rclone.conf"
}

# age_decrypt decrypts an age file with the test's own throwaway identity, through the built age
# image, so the downloaded object can be inspected independently of the drill.
age_decrypt() { # IN OUT
  docker run --rm -i -v "$work:$work" "$age_image" age -d -i "$work/age-key.txt" <"$1" >"$2"
}

wait_for_pg() {
  local c="$1"
  for _ in $(seq 1 90); do
    if docker exec "$c" psql -U "$pg_user" -d "$pg_database" -tAc 'SELECT 1' >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  docker logs "$c" 2>&1 | tail -30 >&2
  echo "backup-postgres-test: $c never accepted connections" >&2
  exit 1
}

wait_for_s3() {
  for _ in $(seq 1 60); do
    if rclone_ctl lsd backup: >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  docker logs "$s3_container" 2>&1 | tail -20 >&2
  echo "backup-postgres-test: the S3 endpoint never answered" >&2
  exit 1
}

# flip_byte flips the low bit of the byte at the middle of FILE, so the ciphertext is provably
# changed without touching the ASCII age header. GNU od/dd only; this test is Linux-only by design.
flip_byte() {
  local file="$1" size offset old new
  size="$(stat -c %s "$file")"
  offset=$((size / 2))
  old="$(od -An -tu1 -j "$offset" -N1 "$file" | tr -d ' ')"
  new=$((old ^ 1))
  printf "\\$(printf '%03o' "$new")" | dd of="$file" bs=1 seek="$offset" count=1 conv=notrunc status=none
}

# ── 1. images and a throwaway age key ────────────────────────────────────
step "images and a throwaway age key (nothing literal ever appears in this script)"
need_image "$pg_image"
need_image "$rclone_image"
need_image "$alpine_image"

docker run --name "$age_build" "$alpine_image" sh -c 'apk add --no-cache age' >/dev/null
docker commit "$age_build" "$age_image" >/dev/null
docker rm -f "$age_build" >/dev/null
ok "age image built from $alpine_image + 'apk add age' (committed, not pulled)"

docker run --rm "$age_image" age-keygen >"$work/age-key.txt" 2>/dev/null
recipient="$(sed -n 's/^# public key: //p' "$work/age-key.txt")"
if [ -n "$recipient" ]; then
  ok "an age key pair was generated into the test's own temp directory"
else
  ko "an age key pair was generated into the test's own temp directory" "$(cat "$work/age-key.txt")"
  exit 1
fi

# ── 2. a seeded, production-shaped source database and a real S3 endpoint ─
step "a seeded source database and a real S3 endpoint (rclone serve s3, SigV4)"
docker network create "$net" >/dev/null

docker run -d --name "$src_container" --network "$net" \
  -e POSTGRES_USER="$pg_user" -e POSTGRES_PASSWORD="$pg_password" -e POSTGRES_DB="$pg_database" \
  "$pg_image" >/dev/null
wait_for_pg "$src_container"

# The four tables the drill's census reads, with the known production row this proof asserts.
docker exec -i "$src_container" psql -U "$pg_user" -d "$pg_database" -v ON_ERROR_STOP=1 >/dev/null <<'SQL'
CREATE TABLE application_users (id uuid PRIMARY KEY, email text NOT NULL);
CREATE TABLE organizations (id uuid PRIMARY KEY, name text NOT NULL);
CREATE TABLE projects (id uuid PRIMARY KEY, name text NOT NULL);
CREATE TABLE data_protection_keys (id uuid PRIMARY KEY, name text NOT NULL);
INSERT INTO application_users (id, email) VALUES ('11111111-1111-4111-8111-111111111111', 'drill@example.invalid');
INSERT INTO organizations (id, name) VALUES ('22222222-2222-4222-8222-222222222222', 'drill-org');
INSERT INTO projects (id, name) VALUES ('33333333-3333-4333-8333-333333333333', 'drill-project');
INSERT INTO data_protection_keys (id, name) VALUES ('44444444-4444-4444-8444-444444444444', 'drill-ring');
SQL
ok "source database seeded with the known row drill@example.invalid and one row in each other table"

docker run -d --name "$s3_container" --network "$net" -v "$work:/w" "$rclone_image" \
  serve s3 --addr :8080 --auth-key access,secret :local:/w/s3 >/dev/null

cat >"$work/rclone.conf" <<EOF
[backup]
type = s3
provider = Other
endpoint = http://$s3_container:8080
access_key_id = access
secret_access_key = secret
force_path_style = true
region = us-east-1
EOF
chmod 600 "$work/rclone.conf"
wait_for_s3
rclone_ctl mkdir "backup:$default_bucket"
rclone_ctl mkdir "backup:$ring_bucket"
rclone_ctl mkdir "backup:$noring_bucket"
ok "rclone serve s3 answered SigV4 and the three test buckets were created"

# ── 3. the default run: no ring requested, the archive holds the dump only ─
step "infra/scripts/backup-postgres.sh (default): dump -> age -> rclone -> bucket"
export PG_BACKUP_RCLONE_BIN="docker run --rm --network $net -v $work:$work $rclone_image"
export PG_BACKUP_AGE_BIN="docker run --rm -i $age_image age"
export RCLONE_CONFIG="$work/rclone.conf"
export PG_BACKUP_CONTAINER="$src_container"
export PG_BACKUP_USER="$pg_user"
export PG_BACKUP_DB="$pg_database"
export PG_BACKUP_WORK_DIR="$backup_work"
export PG_BACKUP_LOG_DIR="$log_dir"
export PG_BACKUP_AGE_RECIPIENT="$recipient"
log_file="$log_dir/backup-postgres.log"

# The ring is opt-in (PG_BACKUP_INCLUDE_RING, default false): with no opt-in the archive carries
# reelforge.dump and nothing else, ring_files=0, and there is no RING_REQUIRED refusal.
set +e
backup_out="$(PG_BACKUP_DEST="backup:$default_bucket" bash "$backup_script" 2>&1)"
backup_status=$?
set -e
if [ "$backup_status" -eq 0 ]; then
  ok "backup-postgres.sh exited 0 (default, no ring requested)"
else
  ko "backup-postgres.sh exited 0 (default, no ring requested) (got $backup_status)" "$backup_out"
fi

objects_json="$(rclone_ctl lsjson "backup:$default_bucket/dumps" 2>/dev/null || echo '[]')"
count="$(printf '%s' "$objects_json" | jq 'length' 2>/dev/null || echo 0)"
name="$(printf '%s' "$objects_json" | jq -r '.[0].Name // empty' 2>/dev/null || true)"
size="$(printf '%s' "$objects_json" | jq -r '.[0].Size // empty' 2>/dev/null || true)"

if [ "$count" = "1" ]; then
  ok "exactly one object under dumps/"
else
  ko "exactly one object under dumps/ (found $count)" "$objects_json"
fi

if printf '%s' "$name" | grep -Eq '^reelbolt-[0-9]{8}T[0-9]{6}Z\.tar\.age$'; then
  ok "the object is named dumps/reelbolt-<ts>.tar.age ($name)"
else
  ko "the object is named dumps/reelbolt-<ts>.tar.age (got '$name')"
fi

log_line="$(tail -n1 "$log_file" 2>/dev/null || true)"
log_cipher="$(printf '%s' "$log_line" | sed -n 's/.*ciphertext=\([0-9]*\)B.*/\1/p')"

if [ -n "$size" ] && [ "$size" = "$log_cipher" ]; then
  ok "the uploaded object's size ($size) equals the ciphertext the run reported ($log_cipher)"
else
  ko "the uploaded object's size equals the ciphertext the run reported (object=$size, log=$log_cipher)" "$log_line"
fi

leftovers="$(find "$backup_work" -type f \( -name '*.dump' -o -name '*.age' \) -print)"
if [ -z "$leftovers" ]; then
  ok "no .dump and no .age file left in the work directory after success"
else
  ko "no .dump and no .age file left in the work directory after success" "$leftovers"
fi

assert_contains "the log line carries the object key" "$log_line" "dumps/$name"
if printf '%s' "$log_line" | grep -Eq 'sha256=[0-9a-f]{64}'; then
  ok "the log line carries a sha256 of the ciphertext"
else
  ko "the log line carries a sha256 of the ciphertext" "$log_line"
fi
assert_contains "the default run reports ring_files=0" "$log_line" "ring_files=0"
assert_not_contains "the default run did not refuse for a missing ring" "$backup_out" "RING_REQUIRED"

# Download the default object and inspect its shape independently of the drill: an age file
# wrapping a tar that holds reelforge.dump and no ring member.
rclone_ctl copyto "backup:$default_bucket/dumps/$name" "$restored_dir/$name"
# The object was written by the rclone container as root; the drill runs as this user.
docker run --rm -v "$work:/w" "$alpine_image" chown -R "$(id -u):$(id -g)" /w >/dev/null

log_sha="$(printf '%s' "$log_line" | sed -n 's/.*sha256=\([0-9a-f]\{64\}\).*/\1/p')"
got_sha="$(sha256sum "$restored_dir/$name" | cut -d ' ' -f1)"
if [ -n "$log_sha" ] && [ "$got_sha" = "$log_sha" ]; then
  ok "the object downloaded from the bucket hashes to the sha256 the run logged"
else
  ko "the object downloaded from the bucket hashes to the sha256 the run logged (bucket=$got_sha, log=$log_sha)"
fi

default_payload="$work/default.payload"
if age_decrypt "$restored_dir/$name" "$default_payload"; then
  default_tar="$(tar -tf "$default_payload")"
  assert_contains "the default archive is a tar holding reelforge.dump" "$default_tar" "reelforge.dump"
  assert_not_contains "the default archive carries no ring (no keys/ member)" "$default_tar" "keys/"
else
  ko "the default object decrypts with the test's own identity"
fi

# ── 4. the opted-in run: the ring is requested and seeded ────────────────
step "infra/scripts/backup-postgres.sh (PG_BACKUP_INCLUDE_RING=true): the ring rides in the archive"
set +e
ring_out="$(PG_BACKUP_DEST="backup:$ring_bucket" PG_BACKUP_INCLUDE_RING=true PG_BACKUP_KEYS_DIR="$keys_dir" \
  bash "$backup_script" 2>&1)"
ring_status=$?
set -e
if [ "$ring_status" -eq 0 ]; then
  ok "backup-postgres.sh exited 0 (ring requested and seeded)"
else
  ko "backup-postgres.sh exited 0 (ring requested and seeded) (got $ring_status)" "$ring_out"
fi

ring_json="$(rclone_ctl lsjson "backup:$ring_bucket/dumps" 2>/dev/null || echo '[]')"
ring_count="$(printf '%s' "$ring_json" | jq 'length' 2>/dev/null || echo 0)"
ring_name="$(printf '%s' "$ring_json" | jq -r '.[0].Name // empty' 2>/dev/null || true)"
if [ "$ring_count" = "1" ]; then
  ok "exactly one object under the ring bucket's dumps/"
else
  ko "exactly one object under the ring bucket's dumps/ (found $ring_count)" "$ring_json"
fi

ring_log="$(tail -n1 "$log_file" 2>/dev/null || true)"
assert_contains "the opted-in run reports ring_files=1" "$ring_log" "ring_files=1"
assert_not_contains "the opted-in run did not refuse (the ring was there)" "$ring_out" "RING_REQUIRED"

# The ring object is an age file wrapping a tar, not a bare dump: decrypt it independently of the
# drill and list its members, so the archive's shape is asserted and not just assumed.
rclone_ctl copyto "backup:$ring_bucket/dumps/$ring_name" "$restored_dir/$ring_name"
docker run --rm -v "$work:/w" "$alpine_image" chown -R "$(id -u):$(id -g)" /w >/dev/null

ring_payload="$work/ring.payload"
if age_decrypt "$restored_dir/$ring_name" "$ring_payload"; then
  ring_tar="$(tar -tf "$ring_payload")"
  assert_contains "the ring archive is a tar holding reelforge.dump" "$ring_tar" "reelforge.dump"
  assert_contains "the ring archive carries the seeded ring file keys/key-test.xml" "$ring_tar" "keys/key-test.xml"
else
  ko "the ring object decrypts with the test's own identity"
fi

# The drill restores the ring archive from the bucket's object alone and reports the ring in its
# census (key_ring_files=1).
step "restore-drill.sh --pg-backup: download from the bucket, decrypt, restore, assert"
drill_age_bin="docker run --rm -i -v $work:$work $age_image age"
set +e
drill_out="$(DRILL_AGE_BIN="$drill_age_bin" bash "$drill_script" \
  --pg-backup "$restored_dir/$ring_name" --age-identity "$work/age-key.txt" \
  --expect-sql "select count(*) from application_users where email = 'drill@example.invalid'" \
  --expect-min 1 2>&1)"
drill_status=$?
set -e
if [ "$drill_status" -eq 0 ]; then
  ok "the drill restored the ring object and exited 0"
else
  ko "the drill restored the ring object and exited 0 (got $drill_status)" "$drill_out"
fi
assert_contains "the census printed the seeded counts" "$drill_out" \
  "application_users=1 organizations=1 projects=1 data_protection_keys=1"
assert_contains "the census reported the ring file the archive carried" "$drill_out" "key_ring_files=1"
assert_contains "the drill asserted the seeded row" "$drill_out" \
  "--expect-sql returned 1, at or above --expect-min 1"

# ── 5. the opted-in run with no ring: fail closed, upload nothing ─────────
step "a run that requests the ring with none present refuses (RING_REQUIRED) and uploads nothing"
set +e
noring_out="$(PG_BACKUP_DEST="backup:$noring_bucket" PG_BACKUP_INCLUDE_RING=true PG_BACKUP_KEYS_DIR="$empty_keys_dir" \
  bash "$backup_script" 2>&1)"
noring_status=$?
set -e
if [ "$noring_status" -ne 0 ]; then
  ok "backup-postgres.sh exited non-zero when the ring was requested and absent (got $noring_status)"
else
  ko "backup-postgres.sh exited non-zero when the ring was requested and absent" "$noring_out"
fi
assert_contains "the refusal names RING_REQUIRED" "$noring_out" "RING_REQUIRED"
noring_json="$(rclone_ctl lsjson "backup:$noring_bucket/dumps" 2>/dev/null || echo '[]')"
noring_count="$(printf '%s' "$noring_json" | jq 'length' 2>/dev/null)"
noring_count="${noring_count:-0}"
if [ "$noring_count" = "0" ]; then
  ok "the refused run uploaded no object (the fail-closed point)"
else
  ko "the refused run uploaded no object (found $noring_count)" "$noring_json"
fi

# ── 6. a byte-corrupted ciphertext makes the drill fail ──────────────────
step "a byte-corrupted ciphertext makes the drill exit non-zero"
cp "$restored_dir/$ring_name" "$restored_dir/corrupt.dump.age"
flip_byte "$restored_dir/corrupt.dump.age"
set +e
corrupt_out="$(DRILL_AGE_BIN="$drill_age_bin" bash "$drill_script" \
  --pg-backup "$restored_dir/corrupt.dump.age" --age-identity "$work/age-key.txt" \
  --expect-sql "select count(*) from application_users where email = 'drill@example.invalid'" \
  --expect-min 1 2>&1)"
corrupt_status=$?
set -e
if [ "$corrupt_status" -ne 0 ]; then
  ok "the drill exited non-zero on a corrupted ciphertext (got $corrupt_status)"
else
  ko "the drill exited non-zero on a corrupted ciphertext" "$corrupt_out"
fi
assert_contains "the failure names the decryption step" "$corrupt_out" "could not decrypt"

# ── 7. an age-wrapped tar with no reelforge.dump member is refused ────────
# A valid, decryptable age file wrapping a valid tar - but the tar holds no reelforge.dump, so
# there is nothing to restore. The drill must refuse it by naming the missing member.
step "an age-wrapped tar with no reelforge.dump member makes the drill exit non-zero"
mkdir -p "$work/other-src"
printf 'not a dump\n' >"$work/other-src/other.txt"
tar --format=posix -cf "$work/other.tar" -C "$work/other-src" other.txt
docker run --rm -i "$age_image" age -r "$recipient" <"$work/other.tar" >"$work/other.tar.age"
set +e
nomember_out="$(DRILL_AGE_BIN="$drill_age_bin" bash "$drill_script" \
  --pg-backup "$work/other.tar.age" --age-identity "$work/age-key.txt" \
  --expect-sql "select count(*) from application_users where email = 'drill@example.invalid'" \
  --expect-min 1 2>&1)"
nomember_status=$?
set -e
if [ "$nomember_status" -ne 0 ]; then
  ok "the drill exited non-zero on a tar with no reelforge.dump member (got $nomember_status)"
else
  ko "the drill exited non-zero on a tar with no reelforge.dump member" "$nomember_out"
fi
assert_contains "the failure names the missing reelforge.dump member" "$nomember_out" \
  "has no reelforge.dump member"

# ── 8. a valid dump whose asserted row is absent fails the assertion (F-QA-4)
# The --expect-sql/--expect-min arm has only ever been seen to pass; this proves it can fail. The
# dump is perfectly valid and restores cleanly, but the row the assertion names is not in it.
step "a valid dump with the asserted row absent fails the drill's assertion (F-QA-4)"
absent_sql="select count(*) from application_users where email = 'nobody@example.invalid'"
set +e
absent_out="$(DRILL_AGE_BIN="$drill_age_bin" bash "$drill_script" \
  --pg-backup "$restored_dir/$ring_name" --age-identity "$work/age-key.txt" \
  --expect-sql "$absent_sql" --expect-min 1 2>&1)"
absent_status=$?
set -e
if [ "$absent_status" -ne 0 ]; then
  ok "the drill exited non-zero when the asserted row was absent (got $absent_status)"
else
  ko "the drill exited non-zero when the asserted row was absent" "$absent_out"
fi
assert_contains "the restore itself succeeded before the assertion failed" "$absent_out" \
  "pg_restore --no-owner --no-privileges --exit-on-error restored the archive as $pg_user"
assert_contains "the failure names the query that came back short" "$absent_out" "$absent_sql"

printf '\n%d passed, %d failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ] || exit 1
