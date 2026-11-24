#!/usr/bin/env bash
# Nightly encrypted backup of the control box's Postgres (docs/postgres-backups.md): pg_dump the
# container's database, pack it - with the Data Protection ring only when asked - into one
# uncompressed POSIX tar, encrypt the tar with age, upload it with rclone and verify the upload.
#
# The archive holds reelforge.dump (the pg_dump -Fc archive, PGDMP). The Data Protection ring
# (keys/key-*.xml) rides in it only when PG_BACKUP_INCLUDE_RING=true, and is left out by default
# on purpose: bundling it means the one age identity unlocks both customer data and the provider
# credentials the ring protects, and a ring re-entered by hand is a recoverable inconvenience
# (docs/disaster-recovery.md), where the dump's contents are not recoverable at all. With the ring
# requested and none found, the run fails closed (RING_REQUIRED), never silently.
#
# The host holds only the age RECIPIENT (a public key); the identity is never on either host. The
# upload check is a COMPLETENESS check against what the backend stored, not an authenticity check:
# age is already AEAD (ChaCha20-Poly1305), and a writer of the bucket controls the reported hash.
#
# What is on disk, and who can read it: mktemp -d creates the work directory under a root-owned
# 0700 parent; it is 0700 and owned by the job user, and holds reelforge.dump and reelforge.tar
# (plaintext, 0600) only until age succeeds, then reelforge.tar.age until a successful run removes
# it. The trap runs on every exit it can catch, SIGINT and SIGTERM included; SIGKILL and power loss
# do not run it, so a stale directory can survive - the next run's startup sweep removes it and
# clears the parent of every backup.* directory left behind, so nothing outlives the next night.
# Configuration (all from the environment; none of it is a secret in this file):
#   PG_BACKUP_CONTAINER/USER/DB optional. Container, role, database (reelbolt-postgres, postgres,
#                         reelforge)
#   PG_BACKUP_DEST        required. rclone path of the destination bucket root, e.g.
#                         reelbolt_backups:reelbolt-backups
#   PG_BACKUP_AGE_RECIPIENT required. age PUBLIC key (age1...); NOT a secret
#   PG_BACKUP_INCLUDE_RING optional. true puts keys/key-*.xml in the archive (default false)
#   PG_BACKUP_KEYS_DIR    optional. Data Protection ring dir (default /opt/reelbolt/keys)
#   PG_BACKUP_AGE_BIN, PG_BACKUP_RCLONE_BIN optional. age, rclone command prefixes
#   PG_BACKUP_LOG_DIR     optional. Log dir (default /var/log/reelbolt)
#   PG_BACKUP_WORK_DIR    optional. Work directory PARENT (default /var/lib/reelbolt)
#   PG_BACKUP_KEEP_LOCAL  optional. true keeps the ciphertext after a successful run
#   PG_BACKUP_VERIFY      optional. false skips the upload verification (default true)
#   RCLONE_CONFIG         optional. rclone config file (default /opt/reelbolt/rclone.conf)
# Flags: --dry-run (encrypt, no upload), --keep-local, -h|--help. Exit 0 after a verified upload of
# dumps/reelbolt-<YYYYMMDDTHHMMSSZ>.tar.age - except that --dry-run and PG_BACKUP_VERIFY=false also
# exit 0 without one, and a second run holding the lock exits 0 having done nothing.

set -euo pipefail

umask 077

container="${PG_BACKUP_CONTAINER:-reelbolt-postgres}"
pg_user="${PG_BACKUP_USER:-postgres}"
pg_db="${PG_BACKUP_DB:-reelforge}"
dest="${PG_BACKUP_DEST:-}"
recipient="${PG_BACKUP_AGE_RECIPIENT:-}"
age_bin="${PG_BACKUP_AGE_BIN:-age}"
rclone_bin="${PG_BACKUP_RCLONE_BIN:-rclone}"
log_dir="${PG_BACKUP_LOG_DIR:-/var/log/reelbolt}"
work_parent="${PG_BACKUP_WORK_DIR:-/var/lib/reelbolt}"
keys_dir="${PG_BACKUP_KEYS_DIR:-/opt/reelbolt/keys}"
include_ring="${PG_BACKUP_INCLUDE_RING:-false}"
keep_local="${PG_BACKUP_KEEP_LOCAL:-false}"
verify="${PG_BACKUP_VERIFY:-true}"
config="${RCLONE_CONFIG:-/opt/reelbolt/rclone.conf}"

usage() {
  sed -n '2,39p' "$0" | sed 's/^# \{0,1\}//'
}

dry_run=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) dry_run=true ;;
    --keep-local) keep_local=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "backup-postgres: unknown argument $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

fail() { echo "backup-postgres: $*" >&2; exit 1; }

[[ -n "$dest" ]] || fail "PG_BACKUP_DEST is not set"
[[ -n "$recipient" ]] || fail "PG_BACKUP_AGE_RECIPIENT is not set"

mkdir -p "$log_dir"
readonly log_file="$log_dir/backup-postgres.log"

# A private, unpredictable work directory. A fixed path under a shared parent is a symlink
# pre-creation and race surface (CWE-377/CWE-59) for a root-run script holding the plaintext dump,
# so create a root-owned parent and let mktemp pick the name. PG_BACKUP_WORK_DIR overrides the
# PARENT, not the directory.
if [[ -L "$work_parent" ]]; then
  fail "the work parent $work_parent is a symlink; refusing to write plaintext through it"
fi
if [[ -e "$work_parent" && ! -O "$work_parent" ]]; then
  fail "the work parent $work_parent is not owned by $(id -un); refusing to write plaintext there"
fi
install -d -m 0700 "$work_parent"
[[ -O "$work_parent" ]] || fail "the work parent $work_parent is not owned by $(id -un); refusing to write plaintext there"
# One run at a time: two overlapping runs would race on the same object key. The loser of the lock
# exits 0 - nothing is wrong, and systemd tries again tomorrow.
exec 9>"$log_dir/backup-postgres.lock"
flock -n 9 || { echo "backup-postgres: another run holds the lock; nothing to do" | tee -a "$log_file"; exit 0; }

# Startup sweep, now that no other run can be active. An interrupted night cannot clean up after
# itself under SIGKILL, so the next run removes what a killed one left: every real backup.*
# directory directly under the parent, and nothing else - no symlink is followed, nothing outside
# the parent is touched.
swept=0
for stale in "$work_parent"/backup.*; do
  [[ -e "$stale" ]] || continue
  [[ -d "$stale" && ! -L "$stale" ]] || continue
  rm -rf -- "$stale"
  swept=$(( swept + 1 ))
done
(( swept == 0 )) || echo "backup-postgres: removed $swept stale work directories left by a killed run under $work_parent" | tee -a "$log_file"

work_dir="$(mktemp -d "$work_parent/backup.XXXXXXXX")"
readonly work_dir

# shellcheck disable=SC2206 # age_bin is deliberately a command prefix ("age", or a container invocation).
readonly -a age_cmd=($age_bin)
# shellcheck disable=SC2206 # rclone_bin is deliberately a command prefix ("rclone", or a container invocation).
readonly -a rclone_cmd=($rclone_bin)

run_rclone() {
  "${rclone_cmd[@]}" "$@"
}

readonly plain="$work_dir/reelforge.dump"
readonly tar_file="$work_dir/reelforge.tar"
readonly cipher="$work_dir/reelforge.tar.age"

succeeded=false
cleanup() {
  # The plaintext (dump and tar) is removed on every exit the trap can catch (EXIT, so SIGINT and
  # SIGTERM included). SIGKILL and power loss do not run it; the next run's startup sweep removes
  # the directory they leave behind.
  rm -f -- "$plain" "$tar_file"
  # A zero-byte ciphertext is never worth keeping: a failed age leaves one behind.
  if [[ -e "$cipher" && ! -s "$cipher" ]]; then
    rm -f -- "$cipher"
  fi
  # A successful run (a verified upload, or a dry run) without --keep-local drops the ciphertext.
  if [[ "$succeeded" == "true" && "$keep_local" != "true" ]]; then
    rm -f -- "$cipher"
  fi
  # If a ciphertext survives - a failure at or after encryption, or --keep-local - keep its
  # directory too and say where it is, so the night's artefact is not lost.
  if [[ -e "$cipher" ]]; then
    echo "backup-postgres: the encrypted dump is kept at $cipher" | tee -a "$log_file"
    return
  fi
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

# Dump. A failing pg_dump, an empty file, a file without the archive magic and a file pg_restore
# cannot list are all refusals: a night that "succeeds" with an unusable archive is only
# discovered during a restore.
docker exec "$container" pg_dump -U "$pg_user" -d "$pg_db" -Fc > "$plain" \
  || fail "pg_dump failed (container=$container database=$pg_db); nothing uploaded"
[[ -s "$plain" ]] || fail "the dump is empty (container=$container database=$pg_db); nothing uploaded"
[[ "$(head -c 5 "$plain")" == "PGDMP" ]] \
  || fail "the dump does not begin with the PGDMP magic; refusing to encrypt a partial archive"
# The magic only proves the header; a dump truncated after it still passes that test. Let
# pg_restore read the whole archive's table of contents and refuse when it cannot.
docker exec -i "$container" pg_restore -l < "$plain" >/dev/null \
  || fail "pg_restore -l could not read the dump (truncated or corrupt); nothing uploaded"

# The Data Protection ring rides in the archive only when PG_BACKUP_INCLUDE_RING=true. When it is
# asked for and absent, fail closed: a run that claims to carry the ring and does not is worse
# than no run at all.
ring_files=0
if [[ "$include_ring" == "true" ]]; then
  shopt -s nullglob
  key_files=("$keys_dir"/key-*.xml)
  shopt -u nullglob
  ring_files=${#key_files[@]}
  (( ring_files > 0 )) || fail "RING_REQUIRED but no key ring files in $keys_dir; nothing uploaded"
fi

# One uncompressed POSIX tar: reelforge.dump, then keys/key-*.xml when the ring was requested.
tar --format=posix -cf "$tar_file" -C "$work_dir" reelforge.dump
if (( ring_files > 0 )); then
  key_names=()
  for k in "${key_files[@]}"; do key_names+=("$(basename "$k")"); done
  tar --format=posix -rf "$tar_file" -C "$keys_dir" --transform='s,^,keys/,' "${key_names[@]}"
fi
tar_size="$(stat -c %s "$tar_file")"

# Encrypt the tar, then delete both plaintext files the instant it succeeds. Never fall back to
# the plaintext.
"${age_cmd[@]}" -r "$recipient" < "$tar_file" > "$cipher" \
  || fail "age failed to encrypt the archive; nothing uploaded"
rm -f -- "$plain" "$tar_file"
[[ -s "$cipher" ]] || fail "the ciphertext is empty; refusing to upload"
[[ "$(head -c 21 "$cipher")" == "age-encryption.org/v1" ]] \
  || fail "the ciphertext does not begin with the age-encryption.org/v1 header; refusing to upload"
cipher_size="$(stat -c %s "$cipher")"
cipher_sha="$(sha256sum "$cipher" | cut -d ' ' -f 1)"

key="dumps/reelbolt-$(date -u +%Y%m%dT%H%M%SZ).tar.age"

if [[ "$dry_run" == "true" ]]; then
  echo "backup-postgres: $(date -u +%Y-%m-%dT%H:%M:%SZ) DRY RUN: encrypted for $key; skipping the upload" \
    | tee -a "$log_file"
  succeeded=true
  exit 0
fi

run_rclone copyto "$cipher" "$dest/$key" --config "$config" --s3-no-check-bucket \
  || fail "rclone failed to upload $key; nothing reported as done"

# The upload is only a backup if it is whole. Compare the object's size against the local
# ciphertext, and its backend-reported hash when the backend has one: a truncated upload that
# happens to preserve the length is exactly the case the hash closes.
if [[ "$verify" == "true" ]]; then
  remote_json="$(run_rclone lsjson --hash "$dest/$key" --config "$config")" \
    || fail "rclone could not read $key back; cannot verify the upload"
  remote_size="$(printf '%s' "$remote_json" | sed -n 's/.*"Size":\([0-9]*\).*/\1/p')"
  [[ -n "$remote_size" ]] || fail "rclone reported no Size for $key; cannot verify the upload"
  [[ "$remote_size" == "$cipher_size" ]] \
    || fail "uploaded size $remote_size != local ciphertext size $cipher_size"
  # S3 and R2 store an MD5 ETag; take whatever hash the backend reports and compare it when there
  # is one, against md5sum of the local ciphertext.
  remote_md5="$(printf '%s' "$remote_json" | sed -n 's/.*"md5":"\([0-9a-f]\{32\}\)".*/\1/p')"
  if [[ -n "$remote_md5" ]]; then
    local_md5="$(md5sum "$cipher" | cut -d ' ' -f 1)"
    [[ "$remote_md5" == "$local_md5" ]] \
      || fail "uploaded md5 $remote_md5 != local ciphertext md5 $local_md5"
  else
    echo "backup-postgres: NOTE: the backend reported no hash for $key; the upload was verified by size only" \
      | tee -a "$log_file"
  fi
fi

# Past the verification the night is good, so the trap may remove the ciphertext and its directory.
succeeded=true

echo "backup-postgres: $(date -u +%Y-%m-%dT%H:%M:%SZ) uploaded $key plaintext=${tar_size}B ciphertext=${cipher_size}B sha256=${cipher_sha} ring_files=${ring_files}" \
  | tee -a "$log_file"
