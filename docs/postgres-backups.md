# Postgres backups

How the control box's Postgres is dumped, encrypted on the host and copied off it nightly, and how to
get it back. The scripts are `infra/scripts/backup-postgres.sh` (the backup) and
`infra/scripts/restore-drill.sh --pg-backup` (the executable restore). For the deployment this runs
in, see `two-box-deployment.md`; for the wider backup picture, see
[`disaster-recovery.md`](disaster-recovery.md).

**Nothing here runs yet.** The units are inert until `/opt/reelbolt/.env` carries the variables, the
bucket exists, and `age` and `rclone` are installed on the control box; the job starts only once the
branch is deployed and the setup below is done.

## What is backed up, and why

Postgres holds all application state: users, organizations, projects, workflows, executions, metering,
billing and the schema itself. The job dumps the whole database with `pg_dump -Fc` and no
`--no-owner`/`--no-privileges`, so the archive is faithful — those flags belong on the restore side,
where the target roles may not exist. Nothing here reconstructs a row; the metering and billing ledgers
exist nowhere else.

The archive holds the customer rows, including `application_users` emails — and nothing else by default:
the Data Protection key ring is **not** in it unless you opt in with `PG_BACKUP_INCLUDE_RING=true` (see
below).

**Where the ring lives, measured.** On the control box, on **2026-10-20**:

| What | Value |
|---|---|
| The running Inference API's environment | `DataProtection__KeysPath=/keys` and **no `DataProtection__Store`** |
| The image's `appsettings.json` | `DataProtection: { KeysPath: "/keys" }`, no `Store` |
| `/opt/reelbolt/.env` | `DATA_PROTECTION_STORE=Database` |
| `docker-compose.control.yml` | sets `DataProtection__Store: ${DATA_PROTECTION_STORE:-FileSystem}`, so it *would* pass `Database` |
| The `reelbolt_dpkeys` volume | one `key-7aa7e137-….xml` |
| `data_protection_keys` in the database | one row |

So the compose file and the running container **disagree**: the container predates that line, or was created
by a different path, and the API is on the default `FileSystem` store. **Both stores hold a key**, which is
what makes this easy to get wrong — the database row is not a leftover, it is the ring under the store the
compose file intends. Read the live answer off the container rather than off this page:

```bash
docker exec reelbolt-inference-1 printenv | sed 's/=.*//' | grep -i dataprotection
```

**A container recreate changes which store is live.** Recreate the API with the compose file as it stands
and the ring moves into `data_protection_keys` — the store
[`disaster-recovery.md`](disaster-recovery.md#the-data-protection-key-ring-the-one-thing-you-cannot-lose)
describes. That matters to this backup, because the dump carries the database rows and nothing else: under
`Database` the dump carries the ring, under `FileSystem` it does not. The ring *files* stay on disk either
way, which is why `PG_BACKUP_INCLUDE_RING` reads them from `PG_BACKUP_KEYS_DIR` and works in both cases.

**A ring-less restore leaves provider keys unreadable.** The default archive carries the dump and no ring,
so restoring it gives you every table — including `inference_providers` — with `api_key_encrypted` values
nothing can decrypt; the operator re-enters those keys in the admin UI, or puts back a copy of
`/opt/reelbolt/keys` (the `dpkeys` volume) that they kept themselves. That copy is small and changes only
when a new ring key is generated. If that is not acceptable for a given deployment, set
`PG_BACKUP_INCLUDE_RING=true` and read the trade-off below first.

The deployment-side story, and why the ring is per-host today, is defect 1 in
`two-box-deployment.md` (section "the Data Protection key ring is per host").

**The ring is opt-in, and it is a trade-off.** With `PG_BACKUP_INCLUDE_RING=true` the archive also carries
the ring files under `keys/`, so a restore can decrypt `inference_providers.api_key_encrypted` directly.
The cost is blast radius: the archive is encrypted to the operator's age identity, so bundling the ring
means that one identity unlocks both the customer data *and* the provider credentials the ring protects.
Left at the default (`false`) the archive holds `reelforge.dump` only and the log reports `ring_files=0`.

**What a restore then needs.** Without the ring in the archive, restore the database and then either
re-enter the provider keys in the admin UI — [`disaster-recovery.md`](disaster-recovery.md) treats that as
a recoverable inconvenience, unlike the dump's contents — or put back a copy of `/opt/reelbolt/keys` you
kept separately. That copy is small and changes only when a new ring key is generated, so it is cheap to
keep outside the bucket. With `PG_BACKUP_INCLUDE_RING=true`, restore the ring files from `keys/` to where
the service reads them (`DataProtection:KeysPath`, `/keys` in the container, the `dpkeys` volume on the
host).

A deployment that *is* switched to `DataProtection:Store=Database` is different: its ring rows are in the
dump and are wrapped — by `DATA_PROTECTION_KMS_LOCAL_KEY` with the local wrapper, or by the AWS KMS CMK —
and that wrapper key must be available to unwrap them.

The database is 62 MB (measured on the control box, 2026-10-20); the encrypted dump is about 18 MB.
`two-box-deployment.md` (section "Back up Postgres") records the earlier **10207 kB** from
2026-10-08. The media box holds no database, and nothing in this work touches it — the two-box split
exists precisely so that box holds no user data.

## Where it lands

The archive lands in the Cloudflare R2 bucket `reelbolt-backups`, under the `dumps/` prefix, at the
object key

```
dumps/reelbolt-<YYYYMMDDTHHMMSSZ>.tar.age
```

The timestamp is UTC. The object is an `age` v1 file (`age-encryption.org/v1` header) wrapping an
uncompressed POSIX tar. By default the tar holds `reelforge.dump` (the `pg_dump -Fc` archive) only; with
`PG_BACKUP_INCLUDE_RING=true` it also holds `keys/key-*.xml`, the Data Protection ring files.

`PG_BACKUP_DEST` is the **bucket root** — `reelbolt_backups:reelbolt-backups` — and the script appends
the `dumps/` prefix itself. Setting it to the prefix (`…/dumps`) nests every object at `dumps/dumps/`,
where the lifecycle rule below no longer matches it.

## How it is encrypted

The archive is encrypted with [age](https://age-encryption.org) on the host, before it leaves the box,
and only the ciphertext is uploaded. There is no plaintext in the bucket, the repository or a second
host, and no path that falls back to writing the plaintext to the destination.

The split is what makes this work:

- The **recipient** (`age1…`) is a public key. It lives in `/opt/reelbolt/.env` as
  `PG_BACKUP_AGE_RECIPIENT`. It is configuration, not a secret: the box can encrypt with it and cannot
  decrypt what it produced.
- The **identity** (`AGE-SECRET-KEY-1…`) is held by the operator out of band — the password manager,
  plus a printed copy. It is the one key that unlocks every backup, and it never lands on either host, in
  the repository or in an image.

What that buys, and what it does not:

| Protects against | Does not protect against |
|---|---|
| A leaked, misconfigured or public bucket — the objects there are ciphertext | A compromised control box **at dump time**: root there reads the plaintext before `age` encrypts it, and can swap the recipient so later nights encrypt to the attacker |
| A stolen dump file — it is ciphertext, useless without the identity | The loss of the identity: without `AGE-SECRET-KEY-1…` every backup is unreadable, and that is unrecoverable — which is why a printed copy exists |
| An R2 token leak — the token can read the ciphertext and delete every backup, but it cannot decrypt one without the identity | Whoever holds the identity reads everything the archive holds — the customer data, and with `PG_BACKUP_INCLUDE_RING=true` the provider credentials the ring protects too. That is the blast radius the ring being opt-in exists to bound |

## Retention

Objects under `dumps/` expire after **30 days**, enforced by one R2 lifecycle rule scoped to the
`dumps/` prefix. The window is chosen for recovery value, not storage cost: 30 dumps of an 18 MB
archive cost almost nothing. It is long enough to cover a bad migration or a deletion nobody notices for
weeks, and it spans a monthly restore-drill cadence so a drill can always run against a real object. It
also bounds how long a second copy of customer emails and wrapped provider keys sits outside the
primary database — 7 days would be defensible on those grounds, 90 would not.

## The schedule and the log

`reelbolt-postgres-backup.timer` fires at **03:40 UTC** — after the blob copy's 03:20, and clear of the
apt-daily window. It has `RandomizedDelaySec=15m`, so the two jobs do not contend for the box's disk or
bandwidth, and `Persistent=true`, so a box that was down at 03:40 runs the backup on the next boot. It
starts `reelbolt-postgres-backup.service`, which runs
`/opt/reelbolt/infra/scripts/backup-postgres.sh` as a one-shot.

Both units are enabled on the control box only. The media box holds no database and must not gain one,
so the units and the script are never installed there.

The run log is `$PG_BACKUP_LOG_DIR/backup-postgres.log`, `/var/log/reelbolt/backup-postgres.log` by
default. A successful run writes one line — the `plaintext` figure is the **tar**, the payload that was
encrypted, not the raw `pg_dump`:

```
backup-postgres: <ISO8601> uploaded dumps/reelbolt-<ts>.tar.age plaintext=<n>B ciphertext=<n>B sha256=<hex> ring_files=<n>
```

The script exits 0 when the dump was produced, encrypted and uploaded and — unless `PG_BACKUP_VERIFY=false`
— the upload matched. The check compares the object's size **and** the hash the backend reports (an S3 or
R2 ETag is an MD5) against the local ciphertext; the `sha256` in the log line is a local computation, and
it is what you check a downloaded object against later. That is a completeness check, not an authenticity
one — `age` is already AEAD, so a tampered object fails to decrypt, and a writer of the bucket controls
any digest it reports. When the backend reports no hash at all the run is verified by size only, and says
so in a `NOTE` line rather than implying a hash check it did not do. Two cases exit 0
without that whole promise: `--dry-run` stops after encrypting and skips the upload, and
`PG_BACKUP_VERIFY=false` skips the verification. A second run holding the lock exits 0 having done nothing.

On success the work directory is removed. On a failure at or after encryption the ciphertext is kept, its
directory with it, and the trap logs `backup-postgres: the encrypted dump is kept at <path>` — a failed
night leaves one ciphertext to inspect or re-upload, never a plaintext dump. **Put an alert on the absence
of an `uploaded` line for a day** before the first paying customer: a backup nobody notices has stopped is
the failure this page exists to prevent.

## Setup

These are operator steps. There is no infrastructure-as-code for the bucket — the tofu tree was deleted
— so create it and its rule with the `cf` CLI.

1. **Create the bucket and the lifecycle rule.** `maxAge` is seconds; `2592000` is 30 days.

   ```bash
   cf r2 buckets create --name reelbolt-backups --location-hint weur
   cf r2 buckets lifecycle update reelbolt-backups --rules '[{"id":"expire-dumps","enabled":true,"conditions":{"prefix":"dumps/"},"deleteObjectsTransition":{"condition":{"type":"Age","maxAge":2592000}}}]'
   ```

   `weur` is Western Europe, where both boxes are (their hostnames end `-weu`). The hint only places the
   bucket's data in a region; it is not a durability or a residency guarantee.

2. **Create the R2 API token** in the Cloudflare dashboard or API, scoped to the `reelbolt-backups`
   bucket with object read and write. Put it in the rclone remote in `/opt/reelbolt/rclone.conf`,
   **mode 0600**, owned by the deploy user. The token never goes into the repository or a container's
   environment; the units carry the rclone *path* (`PG_BACKUP_DEST`), not the credential.

3. **Install the two packages on the control box:**

   ```bash
   apt-get install -y age rclone
   ```

4. **Generate the identity off the box**, in a directory that is not either host:

   ```bash
   age-keygen -o age-key.txt
   ```

   It prints the matching recipient (`# public key: age1…`). Store `age-key.txt` in the password
   manager and print a copy; it is the single point of failure for every dump. Never copy it to a host.

5. **Set the variables in `/opt/reelbolt/.env`.** `PG_BACKUP_DEST` is the bucket root and
   `PG_BACKUP_AGE_RECIPIENT` is the public key from step 4. `PG_BACKUP_INCLUDE_RING` (default `false`)
   decides whether the archive carries the ring: left `false` the archive holds `reelforge.dump` only.
   Set it `true` and the archive also carries `PG_BACKUP_KEYS_DIR` (default `/opt/reelbolt/keys`) — and
   then a missing or empty ring directory is a **refusal** (exit 1, `RING_REQUIRED …`), not a warning, so
   a run that was meant to carry the ring fails loudly rather than shipping a ring-less archive.
   `PG_BACKUP_WORK_DIR` (default `/var/lib/reelbolt`) is the **parent** of the work directory, not the
   work directory itself — the script creates that parent root-owned and mode 0700, then picks an
   unpredictable name inside it, so a local user cannot pre-create the path or point a symlink at it:

   ```
   PG_BACKUP_DEST=reelbolt_backups:reelbolt-backups
   PG_BACKUP_AGE_RECIPIENT=age1...
   PG_BACKUP_INCLUDE_RING=false
   PG_BACKUP_KEYS_DIR=/opt/reelbolt/keys
   PG_BACKUP_WORK_DIR=/var/lib/reelbolt
   ```

   **Check that `PG_BACKUP_KEYS_DIR` exists before you opt in.** The default is the path the ring was
   found at on this box, but the compose file mounts it as the named volume `dpkeys`
   (`docker-compose.yml`), whose host directory is `/var/lib/docker/volumes/reelbolt_dpkeys/_data` — if
   `/opt/reelbolt/keys` is absent, that is the path to point the variable at. One command settles it:
   `ls /opt/reelbolt/keys/key-*.xml || ls /var/lib/docker/volumes/reelbolt_dpkeys/_data/key-*.xml`.
   Whichever is empty means the ring lives at the other, and the script refuses (rather than uploading a
   ring-less archive) if you opt in and the directory has no `key-*.xml`.

   Every `PG_BACKUP_*` name the script reads is documented in its own `--help`; the block is also in the
   repository-root `.env.example`, which the live two-box deployment's `.env` is modelled on — not the
   `infra/compose/` cloud template, which describes a managed database with no container to dump.

6. **Install and enable the units.** The deployment's two systemd units are operator files and are not
   in the public repository, so write your own: a one-shot service that loads the deployment `.env`
   with `EnvironmentFile=` and runs `infra/scripts/backup-postgres.sh`, and a timer that starts it
   nightly. Give the service no `[Install]` section, so that the timer is what gets enabled:

   ```bash
   systemctl daemon-reload
   systemctl enable --now reelbolt-postgres-backup.timer
   ```

7. **Run it once by hand** and read the log, before trusting the timer:

   ```bash
   systemctl start reelbolt-postgres-backup.service
   tail -1 /var/log/reelbolt/backup-postgres.log
   ```

8. **Alert on a missing run.** Watch for the absence of the `uploaded` line for a day (see above).

## Restore

A restore needs the age **identity** (`AGE-SECRET-KEY-1…`) the archive was encrypted to. For the key ring,
the default archive carries none: restore the database and then either re-enter the provider keys in the
admin UI — [`disaster-recovery.md`](disaster-recovery.md) treats that as a recoverable inconvenience — or
put back a copy of `/opt/reelbolt/keys` you kept separately. With `PG_BACKUP_INCLUDE_RING=true` the archive
carries the ring files under `keys/`; restoring them means putting them back where the service reads them
(`DataProtection:KeysPath`, `/keys` in the container, the `dpkeys` volume on the host). A deployment
switched to `DataProtection:Store=Database` is different: its ring rows are in the dump and are wrapped,
so it also needs the wrapper key — `DATA_PROTECTION_KMS_LOCAL_KEY` for the local wrapper, or the AWS KMS
CMK — available to unwrap them.

**Where to run it.** You are in one of two situations. If you already have the identity on your own
machine, run the restore there and give it an `rclone.conf` carrying the same `reelbolt_backups` remote
(the R2 token from the setup above) — that is the recommended path, because the drill needs the identity
present and the identity is the one secret that must never land on a host. Otherwise run it on the
control box, where the `reelbolt_backups` remote is already in `/opt/reelbolt/rclone.conf` and the
PostgreSQL is; the drill still restores only into a throwaway container, but you would have to bring the
identity over for the run. Either way, list the bucket to find the object you want:

```bash
rclone lsf reelbolt_backups:reelbolt-backups/dumps/
```

### The drill

`restore-drill.sh --pg-backup` fetches the object (an rclone path, or a local file), decrypts it with
`--age-identity`, extracts `reelforge.dump` from the tar itself, restores it into a throwaway PostgreSQL,
prints a census of counts and asserts the `--expect-sql` row. Run it against a real object rather than
waiting for a disaster:

```bash
infra/scripts/restore-drill.sh \
  --pg-backup reelbolt_backups:reelbolt-backups/dumps/reelbolt-20261020T034000Z.tar.age \
  --age-identity /path/to/age-key.txt \
  --expect-sql "SELECT count(*) FROM application_users" \
  --expect-min 1
```

`--expect-sql` defaults to `SELECT count(*) FROM application_users` and `--expect-min` defaults to `1`.
The census it prints is counts only — `application_users`, `organizations`, `projects`,
`data_protection_keys` (when the table exists), `public_tables` and `key_ring_files`, the number of ring
files the archive carried (`0` when it carried none) — so nothing it logs can be an email, a key or a
row. The identity file stays on the machine you run the drill from, never on the box. `DRILL_AGE_BIN` and
`DRILL_RCLONE_BIN` override the `age` and `rclone` commands (both are command prefixes, so a container is
spelled `docker run --rm -i <image> age`).

### By hand

To restore without the drill, fetch, decrypt and restore yourself. The restore target is a database you
create for the purpose; `reelforge` — the production database — is **never** the target. The drill
above is the safer default, because it needs no decision at all:

```bash
rclone copyto reelbolt_backups:reelbolt-backups/dumps/reelbolt-<ts>.tar.age ./restore.tar.age \
  --config /opt/reelbolt/rclone.conf
age -d -i ./age-key.txt < ./restore.tar.age > ./restore.tar
mkdir -p ./restore && tar -xf ./restore.tar -C ./restore
docker exec -i reelbolt-postgres psql -U postgres -c 'CREATE DATABASE reelforge_restore'
docker exec -i reelbolt-postgres pg_restore -U postgres -d reelforge_restore \
  --no-owner --no-privileges --exit-on-error < ./restore/reelforge.dump
```

The tar unpacks to `reelforge.dump` — and, with `PG_BACKUP_INCLUDE_RING=true`, `keys/key-*.xml`; copy
those ring files back to the service's `DataProtection:KeysPath` if you need the provider keys to decrypt.
`--no-owner --no-privileges` belong here, on the restore side: the roles the dump names may not exist in
the target. For a plain `pg_dump` and restore on the box, without the bucket,
`two-box-deployment.md` (section "Back up Postgres") has the raw form.

## Verified

**The reproducible check is the hermetic test at the end of this section** — it runs on demand and
exercises the whole chain. The production-dump restore is a **one-time observation on a date**, not a
repeatable check: it proves the chain worked once, on real data, on that day.

**A real production dump, restored — 2026-10-20.** A dump was streamed off the control box —
`docker exec reelbolt-postgres pg_dump -U postgres -d reelforge -Fc`, straight to stdout and never
written to the box's disk — encrypted with a throwaway `age` identity, and restored by the shipped
drill into a throwaway PostgreSQL:

```bash
infra/scripts/restore-drill.sh \
  --pg-backup reelbolt-default.tar.age --age-identity age-key.txt \
  --expect-sql "select count(*) from application_users" --expect-min 1
```

The drill's assertion and census lines, verbatim (the run and step headers are omitted):

```
   PASS  fetched reelbolt-default.tar.age (18272808 bytes)
   PASS  the archive holds reelforge.dump and 0 key ring file(s)
   PASS  the dump is a pg_dump -Fc archive (18262653 bytes)
   PASS  throwaway PostgreSQL up (the image's initdb superuser is postgres)
   PASS  created the NOSUPERUSER role drill owning reelforge_drill and waited for it
   PASS  pg_restore --no-owner --no-privileges --exit-on-error restored the archive as drill

== census (counts only)
   NOTE  census: application_users=5 organizations=9 projects=18 data_protection_keys=1 public_tables=102 key_ring_files=0

== assert a known row
   PASS  --expect-sql returned 5, at or above --expect-min 1 (query: select count(*) from application_users)

== drill complete: 5 check(s) passed
```

The check is a **row count** on `application_users` (5), not a single named row: the default
`--expect-sql` is `SELECT count(*) FROM application_users`, and the census is built to print counts only,
so no email, key or identifier is recorded here or in the run's output. `key_ring_files=0` is the default
archive: it carries no ring, so restoring it means re-entering the provider keys, or putting back a
separately-kept copy of `/opt/reelbolt/keys`. `data_protection_keys=1` is the leftover row in the
database, not the ring. The dump inside the archive was 18,262,653 bytes of `pg_dump -Fc` (623 TOC
entries, 102 of them table data).

**The whole chain, hermetically — the reproducible check.** `infra/scripts/tests/backup-postgres.test.sh`
seeds a database and a key ring, runs the real `backup-postgres.sh` against a real S3 endpoint, downloads
the object back, checks it is a tar holding `reelforge.dump` and the ring file, restores it through the
drill, and asserts the seeded row. It covers the ring in all three shapes — not requested, requested and
present, requested and absent — and the last of those must **refuse and upload nothing**. It then proves
the chain can fail, three ways: a byte-corrupted ciphertext, an age-wrapped tar with no `reelforge.dump`
member, and a perfectly good dump whose asserted row is absent — the last one is what shows the
`--expect-sql` arm is a real check and not a step that has only ever passed. 36 assertions. It needs
Docker and image pulls, so it is run on demand like `restore-drill.sh` itself, not as part of
an operator file.

## What this does not cover

- **Garage objects.** User-uploaded files are copied to a second provider by
  `infra/scripts/backup-blobs.sh`; see
  [`disaster-recovery.md`](disaster-recovery.md#the-nightly-copy-on-a-cloud-host). This page is the
  database only.
- **The media box.** It holds no database and nothing here touches it — that is the point of the
  two-box split.
- **Anything before the deploy.** The bucket and its lifecycle rule must be created, the R2 token put
  in `/opt/reelbolt/rclone.conf`, `apt-get install -y age rclone` run on the control box, the units
  installed and enabled, and the branch deployed. Until then the job does not run at all.
- **R2 itself, and the timer on the box.** `reelbolt-backups` does not exist yet, so no run has
  uploaded through R2 or exercised the lifecycle rule, and the timer has never fired on the control
  box. The S3 path is proven against `rclone serve s3` in the test above, which is a real S3 endpoint
  but not R2's implementation of one.
