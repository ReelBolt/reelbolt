# Disaster recovery

What this platform can lose, what brings it back, and what has actually been proved. WP F13; the
provider choices it builds on are F0's and are locked by decision D19 in
[the ADR](https://github.com/ReelBolt/reelbolt/blob/main/docs/design/cloud-provider-adr.md).

The short version: the database is backed up by the provider and the object copy lives at a second
provider, the Data Protection key ring is in the database where the backup finds it, and the restore
procedure is executable rather than aspirational - `infra/scripts/restore-drill.sh` restores a
database to a point in time and the object store from its copy, and exits non-zero unless every
check passes.

## Targets, and what the current topology actually delivers

F13 sets three targets and they are not all met. This table is the honest ledger; the gap is
explained in the next section, and closing it is a decision for F0's ADR rather than something to
paper over here.

| | Target (F13) | Delivered at S1 | How |
|---|---|---|---|
| Database RPO | 15 minutes | **up to one hour** | Scaleway managed backups run on an interval; one hour is the shortest the engine offers. There is no WAL archiving to restore between backups. |
| Blob RPO | 24 hours | 24 hours | The nightly `rclone` copy of `userFiles` to a second provider. |
| RTO (whole service) | 4 hours | 4 hours, if the operator has done it once | Rebuild the VMs from the images, `.env` and volumes, restore the database from the provider, restore the objects from the copy. Nothing here is billable by the hour, so the budget is operator time. |

Two things this table deliberately does not claim: the RTO has never been measured against a real
account (there is none yet), and the 15-minute database target is **not met** by the S1 topology.

## Why the database RPO is an hour, not fifteen minutes

Scaleway's managed PostgreSQL takes **scheduled logical backups** - an interval in hours, a
retention in days - and **volume snapshots**, which are restorable pictures of the disk. Its own
documentation describes restoration as "restore a backup" or "create an instance from a snapshot",
and nothing anywhere in the product exposes a WAL archive or a `restore_command`. Point-in-time
recovery needs exactly that: a base backup plus every WAL segment up to the instant you want back.
(Checked against Scaleway's Managed Databases reliability page and its RDB API reference on
2026-10-07. The ADR's line "managed Postgres with PITR" is optimistic about what the managed engine
offers; that is a correction for F0 to record, not a bug in this WP.)

So the managed instance can be configured no tighter than its interval, which F13 sets to one hour.
There are two ways to reach fifteen minutes, and both are real:

1. **Run PostgreSQL ourselves with WAL archiving** (`archive_mode = on` and a `restore_command`).
   This is the mechanism the drill exercises end to end - base backup, WAL archive, recovery to a
   timestamp - so the recovering half is already proven; what it needs is a PostgreSQL we operate,
   which is the opposite of what D19 chose for the control plane.
2. **Take a logical dump on a schedule we control** (`pg_dump` hourly to R2). Cheaper than a
   second Postgres, does not need WAL access, and also removes the single-provider dependency on the
   database. It was not built here because it duplicates a backup the provider already takes and it
   is a topology change, not a recovery procedure.

Until one of those is chosen, plan for an hour of database loss and make the object copy carry its
weight: every user upload is at most 24 hours stale, and nothing about the platform's own state
lives outside the database.

## What is backed up, by what

| Thing | Mechanism | Where it lives | Retention |
|---|---|---|---|
| Database (all of it, including provider rows and the key ring) | Scaleway managed scheduled backups, off-region | Scaleway's storage | 14 days |
| Database, second copy (nightly encrypted `pg_dump`) | `infra/scripts/backup-postgres.sh` under `reelbolt-postgres-backup.timer`: age-encrypted on the control box, then uploaded by rclone | Cloudflare R2, `reelbolt-backups/dumps/` — see [`postgres-backups.md`](postgres-backups.md) | 30 days, by bucket lifecycle rule |
| Database disk snapshots | Scaleway snapshots (manual or scheduled) | Same region | Per snapshot |
| Data Protection key ring | With `DataProtection:Store=Database`, rows in `data_protection_keys` - inside the database, so inside its backups. With the default `FileSystem`, the `key-*.xml` files in the `dpkeys` volume instead, which the database backup does **not** carry - see [`postgres-backups.md`](postgres-backups.md#what-is-backed-up-and-why) for which applies and what to do | The database, or `/opt/reelbolt/keys` | With the database, or the copy you keep |
| KMS wrapping key | AWS KMS, one customer-managed key per env | AWS | Configured with a 30-day deletion window |
| User-uploaded objects (`projects/*/userFiles/`) | Nightly `rclone sync` to a bucket at a **second provider** (`infra/scripts/backup-blobs.sh`) | The second provider | Until deleted by hand |
| Rendered outputs and agent artifacts (`outputFiles`, `agentFiles`) | **Not backed up** | - | Regenerated by re-running a workflow; F10 deletes them on a per-plan schedule |
| Runner staging (`runner-staging/`) | **Not backed up** | - | Expires after a day by bucket lifecycle rule |
| RabbitMQ | **Not backed up** - treated as transient | - | F4 re-publishes `Queued` rows that never got a lease |
| Qdrant vectors | **Not backed up** - rebuilt by re-indexing | - | `POST /api/v1/projects/{id}/files/reindex` |
| VMs, network, firewalls, buckets, DNS | **Not backed up** - rebuilt from the images, `.env` and volumes | The repository | n/a |
| Secrets | GitHub environment secrets, delivered to `/opt/reelbolt/.env` | GitHub + the operator's password manager | See [secrets.md](secrets.md) |

The distinction between the two object categories is the whole design: a customer's upload exists
nowhere else, while a rendered video can be produced again from the same inputs. Backing up
`outputFiles` would mean paying to store every render twice for a day of lost render time.

## Restoring the database

The drill's verification half is provider-independent and is what F13's acceptance asks for; the
restore half differs by environment.

### On the managed instance (Scaleway)

1. **Never restore in place.** Create a new instance (or a new database on a new instance) from the
   backup or snapshot closest to, but not after, the moment you want back. An in-place restore
   overwrites production, and the window between the decision and the completion is spent with no
   working database.
2. Give the new instance the same name prefix conventions as the environment and keep its ACL
   closed until step 4.
3. Point the drill at the restored database and let it decide whether it is usable:
   ```bash
   DataProtection__Kms__KeyId="alias/reelbolt-prod-dp-ring" DataProtection__Kms__Region=eu-central-1 \
     AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... \
     infra/scripts/restore-drill.sh --verify-dsn "<restored connection string>" \
       --primary primary:<bucket> --backup reelbolt_backup:<bucket>
   ```
   It fails unless the key ring is present and every encrypted provider key in the restored database
   decrypts. That is the check that matters: a restored database whose provider keys cannot be read
   is a database where nobody can run a workflow.
4. Cut over: stop the control and engine hosts (the operator host script), point
   `DATABASE_CONNECTION_STRING` / `DATABASE_URL` at the restored instance, start again. The
   Inference API migrates on startup, so the restored instance must be at most the release that is
   about to run - restoring an *older* database than the code is fine, restoring a newer one is not.
5. Keep the old instance until the new one has served a day.

### On a PostgreSQL we run ourselves

Restore the base backup into a new data directory, put a `recovery.signal` beside it, and start
with a `restore_command` and a `recovery_target_time`. That is exactly what the drill does, and
its output is the worked example:

```bash
infra/scripts/restore-drill.sh --local      # base backup, WAL archive, recovery to a timestamp
```

## Restoring the objects

The nightly script carries the reverse direction too, so the filter and the verification cannot
drift between taking the copy and putting it back:

```bash
BLOB_BACKUP_SOURCE="primary:<bucket>" BLOB_BACKUP_TARGET="reelbolt_backup:<bucket>" \
  infra/scripts/backup-blobs.sh --restore
```

It copies only `projects/*/userFiles/**`, then verifies that both sides hold the same object count
and byte total. What comes back is the uploads; what does not come back is every rendered output,
every agent artifact and anything under `runner-staging/`. If those matter after a loss - a
customer's delivered video, for instance - re-run the workflow that produced them, or change the
include filter and pay for storing them twice.

## The Data Protection key ring: the one thing you cannot lose

Provider API keys are encrypted with ASP.NET Core Data Protection and the ciphertext is the only
copy: `inference_providers.api_key_encrypted` holds no plaintext, and no log or backup holds one
either. The ring that decrypts them is the rows in `data_protection_keys`, which is why F3 moved it
out of the per-host `/keys` directory and into the shared database - a key ring on one VM's disk is
not in the database backup and does not survive losing that VM.

From F3 onward, each key's XML is wrapped with AWS KMS before it is stored, so the database alone is
not sufficient: the KMS key is the second half. That makes the KMS key the most dangerous object in
the estate, which is why it is configured with a 30-day deletion window. A delete request in the
console leaves a month to change your mind.

Two failure modes worth knowing:

- **The key ring rows are lost** (a database restored from before the ring existed, or a partial
  restore). Every stored provider key becomes unreadable. The fix is to re-enter the API keys in
  `/app/admin/providers`; nothing else in the database is affected, and the drill's census arm is
  the check that catches this before a cutover.
- **The KMS key is deleted.** The wrapped key XML can no longer be unwrapped, so the same keys are
  unreadable even though the rows are intact. Within the 30-day window the key can be restored from
  its pending-deletion state, and everything works again unchanged; after it, re-enter the keys.

Do not rename the application name or the purpose string (`SetApplicationName("ReelForge")`,
`ReelForge.InferenceProvider.ApiKey`): both are part of the key derivation, so renaming them makes
every stored key unreadable in exactly the same way (decision D20, pinned by tests).

## Services that are not backed up

### RabbitMQ is transient

Every workflow's *state* is a row; the queue only carries the request to start it. A broker lost
with un-acked messages leaves rows `Queued` with no message, which is what F4's sweeper fixes: it
re-publishes `Queued` rows older than ten minutes that hold no lease. Rebuild the broker empty
(`docker compose up -d rabbitmq` on the control host) and let the sweeper do its job. Do **not**
purge queues on the way back up: `STARTUP_CLEANUP_PURGE_QUEUES=false` is already the cloud default
(D21), and purging is what would lose the work the sweeper is trying to recover.

### Qdrant is rebuilt by re-indexing

The vectors are derived data. A fresh Qdrant is empty and semantic file search reports
`indexNotReady` rather than failing; re-index per project (`POST
/api/v1/projects/{id}/files/reindex`, or per file) and the assistant's platform-docs collection
rebuilds itself on the next start. Nothing is lost that was not already in the database or the
object store.

## Running the drill

```bash
infra/scripts/restore-drill.sh --local          # stand-ins, no account, no credential
infra/scripts/restore-drill.sh --verify-dsn "<dsn>" --primary primary:<bucket> --backup reelbolt_backup:<bucket>
```

**Local mode** builds everything it needs from containers: a PostgreSQL with `archive_mode = on`,
a real `pg_basebackup`, a real WAL archive, and a recovery to a timestamp; two MinIO endpoints
standing in for the two object stores, driven by the production `backup-blobs.sh` and the real
`rclone`. It needs Docker, `dotnet` and nothing else - no cloud account and no credential - and it
removes everything it created on the way out (`--keep` leaves the work directory for inspection).

It passes only when all of this holds:

| Check | What it rules out |
|---|---|
| The backup script copies `userFiles` and verifies count and bytes | A copy that silently lost objects |
| Only `userFiles` is in the copy | Believing the back-up includes renders it does not |
| The primary bucket is emptied, then restored from the copy, object by object by SHA-256 | A restore that changes bytes |
| `outputFiles` and `runner-staging` are absent after the restore | A copy that quietly holds more than documented |
| A row committed **before** the target time is present after the restore | Recovery that stopped before the WAL it needs |
| A row committed **after** the target time is absent | A "point in time" restore that actually replayed to the end |
| An application table changed after the base backup carries the post-backup value | Recovery that only replayed the marker table |
| The restored key ring decrypts a provider key protected before the backup | A restore that lost or mangled the key ring |
| The same ciphertext does **not** decrypt under a different wrapper | A pass that proves nothing |
| The verify half wrote its own `verifyOk` flag | A drill that silently tested nothing |

**Staging mode** (`--verify-dsn`) is the same verification against a database the operator restored
from the provider's backup or snapshot, and is the shape F13's acceptance names. It runs the census
arm: every encrypted provider key in that restored database must decrypt, and there must be at least
one. It needs the environment the services use - `DataProtection__Kms__KeyId`,
`DataProtection__Kms__Region`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` - and `rclone` on
PATH for the optional bucket comparison.

What the local stand-ins do **not** prove, and what therefore still needs a real account:

- that Scaleway's console or API produces the point-in-time restore F13's acceptance asks for (this
  document argues it does not, and the restore there is "the closest backup or snapshot");
- that Cloudflare R2 and the second provider's S3 endpoints behave as MinIO does under `rclone`
  (checksums, multipart ETags, listing);
- that the KMS-wrapped path works against real AWS KMS - the local drill substitutes the same
  `IKeyWrapper` seam with a local key, which is why it can also demonstrate the failure without it;
- anything about RTO, which is operator time against a real console.

## The nightly copy on a cloud host

The control VM installs `rclone` and arms a systemd timer (`reelbolt-backup.timer`, 03:20 UTC with
a 15-minute spread) that runs the script as a one-shot service. Both units require
`/opt/reelbolt/.env`, so they are inert until the secrets file is delivered, and neither runs on the
engine VM - the timer is only enabled on the control role.

Configuration comes from `/opt/reelbolt/.env` (names in
an operator file):

| Variable | Meaning |
|---|---|
| `BLOB_BACKUP_SOURCE` | rclone path of the primary bucket, e.g. `primary:${MINIO_BUCKET}` |
| `BLOB_BACKUP_TARGET` | rclone path of the second-provider bucket |
| `RCLONE_CONFIG` | `/opt/reelbolt/rclone.conf`, holding the two S3 remotes; mode 0600, delivered out of band like `.env` |
| `BLOB_BACKUP_INCLUDE` | Optional; the default is `/projects/*/userFiles/**` |
| `BLOB_BACKUP_LOG_DIR` | Optional; `/var/log/reelbolt` by default, where `backup-blobs.log` accumulates |

The unit is silent on failure beyond a non-zero exit; the log is the record. Put an alert on the
timer's last-run status (or on the absence of a "done" line for a day) before the first paying
customer: a backup nobody notices has stopped is the failure this whole page exists to prevent.

## Untested without an account

Stated plainly, because F13's acceptance names staging and there is no staging yet:

- the staging half of the drill (`--verify-dsn` against a provider-restored instance) has been run
  only against a throwaway database on a local PostgreSQL, in both directions (it passes with the
  right wrapper, and fails without it);
- no Scaleway, Cloudflare, AWS KMS or second-provider credential has ever been used by this code;
- the systemd timer, the `rclone.conf` delivery and the off-region backup settings are configuration
  and have been validated as configuration only, not exercised on a host.

Everything in the table under "Running the drill" **has** been run (2026-10-07 and 2026-10-08 UTC)
against the local stand-ins described above, and the two ways the checks can fail have each been
reproduced: a census without the KMS wrapper key, and a bucket comparison against a copy the primary
has since outgrown.
