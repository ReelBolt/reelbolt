# Secrets management

Operator reference for every secret the cloud deployment uses: what it is, how it reaches a VM,
and how to rotate it. The goal is that no secret lives in a manifest or in the repository.
Slice S1 runs on two VMs with `docker compose` (decision D19), so delivery is
designed for VMs first; the Kubernetes plan for S2 is the last section. For the storage side see
[`cloud-storage.md`](cloud-storage.md); for the VM layout see
`infra/README.md` and
[`ci.md`](ci.md).

## Rules

- A secret is a value whose disclosure lets someone spend money, read customer data or act as
  the platform. Identifiers (an AWS KMS key id, a bucket name, a hostname) are configuration, not
  secrets, but they travel in the same file for simplicity.
- Secrets are never committed, never passed as build arguments, never in an image. The repository
  holds names and placeholders only (`infra/compose/env.*.example`).
- The single source of truth for each secret's value is your password manager. GitHub
  environment secrets are write-only (you can replace a value, not read it back), so they are a
  delivery channel, not a vault.
- Secrets reach the VMs only over ssh, with file mode 0600, owned by the deploy user.
- Never log a value. `host.sh check-env` reports variable names only.
- The CI step `infra/scripts/check-secrets.sh` fails a build when infra, compose or workflow files
  contain what looks like a literal secret (see "Literal-secret check").

## Secret inventory

Names are the `.env` variable names; the application setting each maps to is in
`docker-compose.yml`. "Both" means the control and engine hosts must hold the **same** value.

| Secret | Variable(s) | Used by | Host | Notes |
|---|---|---|---|---|
| JWT signing key (session key) | `JWT_SIGNING_KEY` | Go API (issues), Inference API (validates), web | Control | HS256, 32+ characters. Single key, no overlap window: see "Rotating the JWT signing key". `host.sh check-env` refuses it in the engine host's `.env`. |
| Engine token key | `ENGINE_JWT_SIGNING_KEY` | Engine (signs its 5-minute on-behalf-of tokens, validates the Inference API's admin tokens), Inference API (both directions) | Both | 32+ characters, must differ from `JWT_SIGNING_KEY` (services and `host.sh check-env` refuse equal keys). `ENGINE_JWT_ACCEPT_LEGACY_OBO=true` on control is for the cut-over only. |
| Database credentials | `DATABASE_CONNECTION_STRING` (Npgsql), `DATABASE_URL` (Go API) | Inference API, engine, Go API | Control (administrator login), engine (Npgsql string as `reelbolt_engine`) | On Scaleway managed PostgreSQL, `sslmode=require`. The engine host holds only its least-privilege login; `ENGINE_DB_PASSWORD`, `GATEWAY_DB_PASSWORD` and the optional `ENGINE_MIGRATOR_DB_PASSWORD` sit on control, where `infra/scripts/apply-db-roles.sh` applies them. `POSTGRES_PASSWORD` is the self-host container's only. See an operator document. |
| RabbitMQ credentials | `RABBITMQ_USER`, `RABBITMQ_PASSWORD` | RabbitMQ container, Go API, Inference API, engine | Both, a different user per host | Each host has its own broker user; the engine host's user is never the control one (runbook step 9). The broker listens on the control VM's private address. The broker applies the default user from these variables only when its volume is first created. `RABBITMQ_PASSWORD` is required by `docker-compose.yml` (no `guest` fallback), and `host.sh check-env` refuses `guest` and any value known from the repository (`infra/compose/known-secret-hashes.txt`, also covering `JWT_SIGNING_KEY`, `ENGINE_JWT_SIGNING_KEY`, `ADMIN_PASSWORD`). |
| R2 object-store key pair | `MINIO_ACCESS_KEY`, `MINIO_SECRET_KEY` | Inference API, engine | Both, a different token per host | Two bucket-scoped R2 tokens, one for control and one for the engine host ([`cloud-storage.md`](cloud-storage.md) "API tokens"). The control key never goes in the engine host's env file. |
| Inference provider keys (environment) | `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `DEEPSEEK_API_KEY`, `AZURE_OPENAI_API_KEY` | Inference API, engine (only when a provider row's key is the literal `env:`, or for the legacy Azure fallback) | Both | Optional. Keys typed into the admin provider form are not here: they live encrypted in the database. A Pro/Max subscription token must not be used in a deployment that serves other people. |
| Inference provider keys (database) | stored per `inference_providers` row | Resolved by both services | Database | Encrypted with ASP.NET Data Protection. Rotated in the admin UI, see "Rotating inference provider keys". |
| AWS credentials for KMS | `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_REGION` | Inference API and engine, to wrap and unwrap the Data Protection key ring | Both | Access key of the `*-kms-wrapper` IAM user, held in the `.env` files and the GitHub environment secrets. |
| ipinfo.io token | `IPINFO_TOKEN` | Inference API (`Billing:FirstVideoGrant:Abuse:IpInfoToken`) | Control | Optional. Sent as a bearer header, never logged or put in a URL. Empty skips the VPN/proxy/hosting check for the first-video grant. |
| Data Protection KMS key id | `DATA_PROTECTION_KMS_KEY_ID` | Same | Both | An identifier, not a secret. Required unless `DATA_PROTECTION_KMS_LOCAL_KEY` is set. |
| Data Protection local wrapper key | `DATA_PROTECTION_KMS_LOCAL_KEY` | Same | Self-host only | Base64, 32 bytes. Not for the cloud; see "Rotating the Data Protection wrapper". |
| Assistant confirmation-token key | none (stored in the `platform_secrets` table, wrapped as above) | Inference API | Database | One key per deployment, generated on first use. No operator action. |
| SMTP credentials | `SMTP_USERNAME`, `SMTP_PASSWORD` | Go API | Control | Optional. Without SMTP the API logs action links only with `APP_ENV=development`, and never the marketing confirmation link. |
| Mail log reference key | `MAIL_LOG_REF_KEY` (Inference config `Mail__LogRefKey`) | Go API, Inference API | Control | Optional. Keys the `rcpt=` reference that mail logs carry instead of an address. Unset, each process uses a random key and logs a warning, so references do not correlate. Rotating it only breaks correlation with older log lines. See [`marketing-consent.md`](marketing-consent.md#log-references). |
| Operator digest address | `OPERATOR_DIGEST_EMAIL` (Inference config `Operations__FailureDigest__RecipientEmail`), with `OPERATOR_DIGEST_ENABLED` | Go API, Inference API | Control | Optional, and not a secret, but personal data: keep it out of logs and tickets. Both services must hold the same value; the Go API relays the daily `operator_digest` mail to that address only. See [`platform-kill-switches.md`](platform-kill-switches.md#the-daily-operator-digest). |
| Initial admin password | `ADMIN_PASSWORD` | Go API, first boot with an empty database | Control | Needed once. Delete it from the file afterwards. |
| Sandbox executor token | `SANDBOX_API_TOKEN` | Engine and sandbox executor (same host) | Engine | Required while S1 runs the sandbox on the engine VM. Self-host and S1 only; it goes away with the runner fabric. |
| Import worker token | `IMPORT_WORKER_TOKEN` | Inference API and import-worker | Control | Mandatory: `docker-compose.yml` uses `${IMPORT_WORKER_TOKEN:?}`, so every compose invocation on every box (including the media box) fails without it. Must be in the production `CONTROL_ENV_FILE` and `ENGINE_ENV_FILE` before the next production deploy. Random 32 bytes |
| Qdrant API key | `VECTOR_QDRANT_API_KEY` | Inference API | Control | Only when using hosted Qdrant (WP F7). The bundled container has no key and is not published. |
| OTLP export credentials | `OTEL_EXPORTER_OTLP_HEADERS` (and the non-secret `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`) | Go API, Inference API, engine | Both | Optional. The header carries the Grafana Cloud `Authorization=Basic ...` value (instance id plus a token scoped to metrics and traces write), so it is a secret; blank endpoint means nothing is exported. See [`observability.md`](observability.md). |
| Grafana credentials | `GRAFANA_PROMETHEUS_TOKEN`, `GRAFANA_TEMPO_TOKEN`, `GRAFANA_POSTGRES_PASSWORD`, `GRAFANA_ADMIN_PASSWORD` (and the non-secret `GRAFANA_PROMETHEUS_URL`/`_USER`, `GRAFANA_TEMPO_URL`/`_USER`, `GRAFANA_POSTGRES_HOST`/`_PORT`/`_DATABASE`/`_USER`/`_SSLMODE`, `GRAFANA_ADMIN_USER`, `GRAFANA_PORT`) | Grafana (the opt-in `observability` profile) | Control | Required when the control host runs Grafana: `host.sh check_env` refuses to start when the admin password is blank, a placeholder or a shipped default, when `GRAFANA_POSTGRES_USER` is blank or the database superuser, or when a datasource URL is set without its token. `GRAFANA_POSTGRES_USER` must be the **read-only** role `infra/grafana/postgres-readonly-role.sql` creates, never the database superuser: the two database-backed dashboards read the billing ledger (`usage_events`; `credit_ledger_entries` joined to `workflow_step_results`), and the script grants that role `SELECT` on exactly those three tables only. The script creates the role with no password; set its password out of band after it runs and put that value in `GRAFANA_POSTGRES_PASSWORD`. `GRAFANA_ADMIN_PASSWORD` must be set: an empty or absent value leaves Grafana's built-in `admin` default. See `../infra/grafana/README.md`. |
| Paddle billing credentials | `PADDLE_API_KEY` (config `Billing__Paddle__ApiKey`), `PADDLE_WEBHOOK_SECRET` (`Billing__Paddle__WebhookSecret`); non-secret `BILLING_ENABLED`, `PADDLE_ENVIRONMENT`, `PADDLE_TOPUP_PRICE_500`/`_1000`/`_3000` | Inference API | Control | Only with `BILLING_ENABLED=true`; `host.sh check-env` then requires both secrets. The API key is a sandbox (`pdl_sdbx_apikey_...`) or live key and only works against its own environment. The webhook secret is the notification destination's secret key: Paddle signs every webhook with it, and anyone holding it can forge billing events, so rotate it in Paddle (it supports two active secrets during rotation) and then here. See [`billing.md`](billing.md). |
| Stripe billing credentials | `STRIPE_API_KEY` (config `Billing__Stripe__ApiKey`), `STRIPE_WEBHOOK_SECRET` (`Billing__Stripe__WebhookSecret`), optional `STRIPE_WEBHOOK_SECRET_PREVIOUS` (`Billing__Stripe__WebhookSecretPrevious`); non-secret `BILLING_PROVIDER`, `STRIPE_MANAGED_PAYMENTS`, `STRIPE_PORTAL_CONFIGURATION_ID`, `STRIPE_RETURN_BASE_URL`, `STRIPE_API_BASE`, `STRIPE_TOPUP_PRICE_500`/`_1000`/`_3000` | Inference API | Control | Only with `BILLING_ENABLED=true` and `BILLING_PROVIDER=Stripe`; `host.sh check-env` then requires the key, the webhook secret and the non-secret values, and refuses `CHANGE_ME`. The API key is a restricted key (`rk_test_...`, `rk_live_...`) with only the permissions the service uses, or a secret key (`sk_...`); the prefix selects test or live mode and a test key only sees test objects. The webhook secret (`whsec_...`) belongs to one webhook endpoint in one mode, and anyone holding it can forge billing events. Rotate it by rolling the signing secret in the Stripe Dashboard (Stripe signs with both for up to 24 hours), putting the old value in `STRIPE_WEBHOOK_SECRET_PREVIOUS` and the new one in `STRIPE_WEBHOOK_SECRET`, deploying, and clearing the previous one afterwards. Use a different key for `tools/stripe-catalog-sync` (it is passed in the shell environment of the person running it, never stored on the host). See [`billing.md`](billing.md#api-keys-stripe). |
| Cloudflare Email Sending credentials | `CLOUDFLARE_EMAIL_TOKEN` (a secret), `CLOUDFLARE_EMAIL_ACCOUNT_ID` | Go API, and the contact form (the `site` container) | Control | The outbound mail provider. The contact form sends through it via `site/lib/marketing/mailer.ts`; there is no webhook URL. The token is set by the operator in the env file and is never in the repo. See [`marketing-site.md`](marketing-site.md#contact-form-delivery). |
| Brevo campaign API key | `BREVO_CAMPAIGN_API_KEY` (a secret) | Inference API | Control | The marketing campaign sender (Brevo campaigns API; `Marketing:Campaigns:BrevoCampaignApiKey`). A new variable; `BREVO_API_KEY` is not read. Never committed, never logged. `BREVO_WEBHOOK_TOKEN` (a secret) is the header token for the Brevo webhook. See [`marketing-consent.md`](marketing-consent.md#campaigns-brevo). |
| Social publishing OAuth clients | `SOCIAL_YOUTUBE_CLIENT_ID`, `SOCIAL_YOUTUBE_CLIENT_SECRET` (config `SocialPublishing__YouTube__ClientId`/`ClientSecret`), `SOCIAL_LINKEDIN_CLIENT_ID`, `SOCIAL_LINKEDIN_CLIENT_SECRET` (`SocialPublishing__LinkedIn__*`); non-secret `PUBLIC_BASE_URL` | Inference API | Control | Optional. A platform is hidden and its connect route answers `503 provider_not_configured` until both its id and secret are set. The redirect URI to register with each platform is `{PUBLIC_BASE_URL}/api/v1/social/{youtube\|linkedin}/oauth/callback`. The secrets are never logged; the per-account access and refresh tokens are stored encrypted with Data Protection. |
| Deploy ssh key | `SSH_PRIVATE_KEY` | `deploy.yml` | GitHub environment | Plus `SSH_KNOWN_HOSTS` (public host keys, pinned) and `CONTROL_HOST`/`ENGINE_HOST`. |
| Postgres backup age key pair | `PG_BACKUP_AGE_RECIPIENT` (the recipient, a variable); the identity is a file, not a variable | `infra/scripts/backup-postgres.sh` (encrypts), and the operator who restores a dump (decrypts) | Control holds the recipient only; the identity is on neither host | The recipient (`age1…`) is a **public** key — configuration, not a secret — and lives in `/opt/reelbolt/.env`. The matching identity (`AGE-SECRET-KEY-1…`) is held by the operator out of band (the password manager, plus a printed copy) and must never land on either host, in the repository or in an image: it is the single point of failure for every dump, and its loss is unrecoverable. See [`postgres-backups.md`](postgres-backups.md). |
| rclone remote credentials | the file `/opt/reelbolt/rclone.conf` (mode 0600; no environment variable) | `infra/scripts/backup-blobs.sh`, via the `reelbolt-backup.timer`/`reelbolt-backup.service` units | Control | WP F13. The rclone config holding the credentials of the remotes it syncs to and from: the primary bucket's remote and the **second (backup) provider**'s bucket — the nightly off-provider copy of the irreplaceable objects. It also holds the `reelbolt_backups` remote for the nightly encrypted Postgres dump ([`postgres-backups.md`](postgres-backups.md)), driven by `reelbolt-postgres-backup.timer`/`reelbolt-postgres-backup.service`. It never reaches a container's environment or the `.env` files: the timer's `BLOB_BACKUP_SOURCE`/`BLOB_BACKUP_TARGET` variables carry rclone *paths*, not the remotes. Rotated out of band: replace the file on the control host (mode 0600, owned by the deploy user) with a fresh rclone config at the providers, then the next nightly run (or `systemctl start reelbolt-backup.service`) picks it up and the old credentials are deleted at the providers. |
| Environment files | `CONTROL_ENV_FILE`, `ENGINE_ENV_FILE` | `deploy.yml` writes them to `/opt/reelbolt/.env` | GitHub environment | The delivery channel for everything marked Control or Engine above. |
| Cloudflare Pages token (legacy, unused) | `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` | `deploy.yml` `pages` job | GitHub environment | The marketing site is not deployed to Pages, so leave these unset. The job skips without them. |
| Registry pull | GitHub Actions `GITHUB_TOKEN` | VMs, per deploy | Ephemeral | Minted for each run with `packages: read`; no registry token is stored. |

Declared by later work packages and **not in the code yet**. Each adds its name here and to
`check_env` in `infra/compose/host.sh` when it lands:

| Secret | Work package | Note |
|---|---|---|
| Runner pool credential | D (gateway), cloud workers | The pool worker's `hello` credential (`docs/runner-protocol.md`). |
| Gateway job-token key | D (gateway) | Signs per-job tokens. |
| Engine-to-gateway service token | D (gateway) | Protects the gateway's internal API. |
| `TURNSTILE_SECRET` | C | Optional bot check on signup. |
| OAuth client secrets | C10 | Google and GitHub sign-in, later. |

## How secrets reach a VM

**Decision (S1): one GitHub environment secret per host holds that host's whole `.env`, and
`deploy.yml` writes it to `/opt/reelbolt/.env` over ssh with mode 0600 before each deploy.**

The two secrets are `CONTROL_ENV_FILE` and `ENGINE_ENV_FILE`, defined per GitHub environment
(`staging` and `production`), next to the existing `SSH_PRIVATE_KEY`.

Why this and not a SOPS-encrypted file in the repository:

- GitHub is already the trust root for deploys. The ssh key that can reach both VMs lives in the
  same environment secrets, so storing the env files there adds no new party and no new
  key to lose or leak.
- Production's required reviewers already gate the `production` environment's secrets, so a pull
  request cannot read or change them. A SOPS file would need its own key (age or KMS) distributed
  to CI, and an encrypted blob in the repository is still a blob forever in history.
- There is no new tooling: a rotation is "replace one secret, re-run the deploy". With SOPS it
  is a key ceremony plus a commit.
- The cost is real and accepted: GitHub secrets are write-only, so the password manager must hold
  the canonical file, and there is no review of a secret change in a pull request. Both are
  tolerable for one operator and two hosts. The S2 design (below) replaces this channel.

How it works (see `.github/workflows/deploy.yml` and `infra/compose/host.sh`):

1. Before a host's deploy step, the workflow pipes the secret to
   `umask 077 && cat > .env.new`, copies the previous file to `.env.bak`, and renames the new one
   into place. The write is atomic and the file is never world-readable, even for an instant.
2. `host.sh deploy` runs `check_env` first, before it pulls an image or touches a container.
   It requires mode 0600 (and fixes it), LF line endings, every required name present, no
   `CHANGE_ME` placeholder, no development default for the RabbitMQ and object-store secrets, a
   JWT key of 32+ characters, and a Data Protection wrapper (KMS key id or local key) with AWS
   credentials when KMS is used. A failure exits non-zero and the running stack is untouched.
3. `docker compose up -d` then recreates exactly the containers whose environment changed.
4. If the secret is unset, the workflow keeps the file already on the VM, still validates it, and
   prints a notice. A host set up by hand keeps working.

First-time setup, per environment:

1. Copy `infra/compose/env.control.example` and `env.engine.example` to your password manager
   (not into the repository) and fill every `CHANGE_ME`. Generate secrets with `openssl rand -hex
   32`. The values marked "same as control" must match in both files.
2. Paste each filled file into the GitHub environment as `CONTROL_ENV_FILE` and `ENGINE_ENV_FILE`.
3. Run the deploy. To validate without deploying, ssh to a host and run
   `REELBOLT_ROLE=control bash /opt/reelbolt/infra/compose/host.sh check-env` (role `engine` on the
   engine host).

Adding or changing one variable: edit the canonical file in your password manager, paste the whole
file into the GitHub secret, re-run the deploy workflow for that environment. To roll back a bad
file, copy `/opt/reelbolt/.env.bak` over `/opt/reelbolt/.env` on the host and run
`host.sh deploy <current sha>`.

Escape hatch: `scp` a file as `/opt/reelbolt/.env` with mode 0600 and leave the GitHub secret
unset. This is the pre-F8 behaviour and what the first boot of a fresh VM uses.

## Rotation basics

Every runbook below follows the same shape: create the new value, put it in the canonical file
and the GitHub secret(s) for **both** environments' hosts that use it, run the deploy, verify,
then revoke the old value. Where the dependency allows two live values at once the rotation has
no downtime; where it does not, the section says so.

To roll a value, edit `CONTROL_ENV_FILE` (and `ENGINE_ENV_FILE` when the secret is shared),
then run `Deploy` for that environment from the Actions tab (a dispatch with an empty `sha`
redeploys the tip of `master`; a repeated SHA still recreates containers whose environment
changed). Check `/health`, `/api/v1/health` and `/api/v1/workflow-engine/health` afterwards;
the deploy workflow already does and rolls code back on failure.

No rotation drill has been run on a live staging environment yet (no cloud account exists at the
time of writing). Run each section once on staging after the first deploy, and record the date
in the status notes.

## Rotating the JWT signing key

There is **no overlap window today**. The Go API signs with and validates against one key
(`JWTSigningKey` in `api/services/jwt_service.go`), and the Inference API (`IssuerSigningKey` is a
single `SymmetricSecurityKey`) and the web proxy do the same. With `ENGINE_JWT_SIGNING_KEY` set, the
engine does not hold or check this key. A new key
therefore invalidates every issued token the moment a service restarts with it.

What that costs: every logged-in user is signed out (tokens last 24 hours) and must log in again;
the engine's calls are signed with the engine key, so they are not affected and only the control
host is deployed.

Runbook, at a quiet time:

1. `openssl rand -hex 32`; store it in the password manager.
2. Put it in `JWT_SIGNING_KEY` in the **control** env file and its GitHub secret. The engine host
   does not hold this key (it holds `ENGINE_JWT_SIGNING_KEY`), so its file stays unchanged.
3. Run the control deploy.
4. Confirm a fresh login works and an execution started after the deploy completes.
5. Tell users nothing; they simply sign in again.

**Follow-up (not built):** a zero-downtime rotation needs a second, validate-only key.
Concretely: `Jwt__PreviousSigningKey` / `JWT_PREVIOUS_SIGNING_KEY`, accepted by
`TokenValidationParameters.IssuerSigningKeys` in both .NET services and tried second by the Go
validator, never used to sign. The rotation then becomes: add the new key as primary and the old
as previous, deploy, wait 24 hours (the token lifetime), remove the previous key. Until that ships,
the table above is the real procedure.

## Rotating the database password

Scaleway managed PostgreSQL allows several users, which gives a zero-downtime rotation.

1. In the Scaleway console create a second database user with the same privileges on the
   application database, with a new password from `openssl rand -base64 24`.
2. Put the new user and password into `DATABASE_CONNECTION_STRING` and `DATABASE_URL`
   (control); keep `sslmode=require`. The engine host's string uses `reelbolt_engine`, not this
   user: rotate that one as described in
   an operator document.
3. Run the deploy, then check the three health endpoints.
4. Delete the old database user in the console after the deploy is healthy.

## Rotating the RabbitMQ password

The broker container creates its default user from `RABBITMQ_USER`/`RABBITMQ_PASSWORD` **only on
the first start of an empty volume**, so editing the variable alone does not change the broker.
There is a short interruption: services reconnect once they restart with the new value.

1. Generate a value with `openssl rand -hex 24`.
2. On the control VM:
   `docker compose --env-file .env --env-file .deploy.env -f docker-compose.yml -f infra/compose/docker-compose.cloud.yml -f infra/compose/docker-compose.cloud.control.yml exec rabbitmq rabbitmqctl change_password <user> '<new password>'`.
   Rotate one host's user at a time: `<user>` is that host's own broker user.
3. Immediately put the value in `RABBITMQ_PASSWORD` in that host's env file only and deploy that host,
   so its consumers pick it up. Never copy a password from one host's file into the other's. MassTransit retries the connection until then.
4. Verify that an execution queues and completes.

## Rotating the R2 object-store keys

No downtime. The application token is separate from every other credential (see
[`cloud-storage.md`](cloud-storage.md) "API tokens").

Rotate each host's token on its own; never write the control pair into the engine host's file.

1. In Cloudflare create a new R2 token with the same scope (Object Read and Write, the one bucket)
   for the host you rotate.
2. Put the new pair in `MINIO_ACCESS_KEY` and `MINIO_SECRET_KEY` in that host's env file only
   (`CONTROL_ENV_FILE` or `ENGINE_ENV_FILE`) and deploy that role.
3. Run the staging smoke checklist in `cloud-storage.md`, steps 1 to 5.
4. Wait at least 30 minutes: presigned URLs minted with the old key live up to 30 minutes and stop
   working the moment the old token is deleted.
5. Delete that host's old token in Cloudflare.

## Rotating inference provider keys

**Keys stored in the admin UI** (`inference_providers` rows). In `/app/admin/inference-providers`
open the row, paste the new key and save; the old one is overwritten (an omitted key means
"unchanged", an empty one clears it). The resolver caches rows for 60 seconds
(`Inference:ProviderCacheSeconds`), so the change applies within about a minute with no deploy.
Revoke the old key at the vendor afterwards.

**Keys in the environment** (`ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `DEEPSEEK_API_KEY`,
`AZURE_OPENAI_API_KEY`, used through the `env:` sentinel): create a new key at the vendor, put it
in both env files, deploy, run the provider's connection test in the admin UI, revoke the old key.

## Rotating the AWS KMS access key and the Data Protection key ring

**The IAM access key** (`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`): an IAM user may hold two
access keys, so there is no downtime.

1. In the AWS console create a second access key for the `*-kms-wrapper` user.
2. Put it in both env files, deploy.
3. Confirm both services start (a wrong key fails at the first key-ring read) and a provider's
   connection test passes.
4. Delete the old access key.

**The KMS key itself** rotates automatically each year. AWS keeps the old key material, so existing
wrapped keys keep decrypting and no re-wrap is needed. Never delete the KMS key or schedule it for
deletion: every stored provider API key becomes unreadable (the same consequence as renaming the
Data Protection application name or the `ReelForge.InferenceProvider.ApiKey` purpose, decision D20).

## Rotating the Data Protection wrapper

`DATA_PROTECTION_KMS_LOCAL_KEY` is for self-host only. It wraps the key ring with a 32-byte local
key. There is no re-wrap tool: replacing the value makes the existing wrapped keys undecryptable,
and with them every stored provider key. Do not rotate it. If it must change, first export the
provider keys from the admin UI (or re-enter them afterwards), and expect to re-enter all of them.
This is a known limit and a follow-up (a re-wrap command), and the cloud avoids it by using KMS.

## Rotating the SMTP, Qdrant and mail-provider secrets

Each is an independent value held at a third party; none is shared between the two VMs. None
of them is set in two places.

- **SMTP:** create new credentials at the mail provider, set `SMTP_USERNAME`/`SMTP_PASSWORD`
  in the control env file, deploy, send a test (an invite or a password reset), revoke the old
  credentials.
- **Qdrant API key** (hosted Qdrant only): create a second key in the Qdrant Cloud console,
  set `VECTOR_QDRANT_API_KEY` in the control env file, deploy, check that file search returns
  results (not `indexNotReady`), delete the old key.
- **Cloudflare Email Sending token:** create a new Cloudflare user API token with the account's
  email-sending permission, set `CLOUDFLARE_EMAIL_TOKEN` in the control env file, deploy, submit the contact form once (which answers "Failed to
  deliver message" until the token is valid), then revoke the old token.

## Rotating the sandbox executor token

`SANDBOX_API_TOKEN` is shared by the engine and the sandbox executor, both on the engine VM, so a
single host changes. Active sandbox sessions end when the containers restart, so do it between
runs. Put a new `openssl rand -hex 32` value in `ENGINE_ENV_FILE`, deploy, and run one execution
that uses Remotion. Self-host: change `.env` and `docker compose up -d`.

## Rotating the deploy credentials

- **`SSH_PRIVATE_KEY`:** generate a new key pair (`ssh-keygen -t ed25519`), add the new public
  key to `authorized_keys` on both VMs, replace the GitHub secret, run a deploy, then remove the
  old public key from both VMs.
- **`SSH_KNOWN_HOSTS`:** only changes when a VM is rebuilt; regenerate with `ssh-keyscan -t ed25519`
  against each host and verify the fingerprints out of band.

## Rotating the admin bootstrap password

`ADMIN_PASSWORD` seeds the first admin only when the user table is empty, and a password change
is forced on first login. After the first boot, delete the line from the env file (and the GitHub
secret) and deploy. Rotate the admin's real password in the application.

## Literal-secret check

`infra/scripts/check-secrets.sh` runs in the `infra-lint` job of `ci.yml` and scans `infra/`,
`.github/workflows/` and `docker-compose.yml`. It fails on well-known token shapes (AWS access
key ids, GitHub tokens, `sk-ant-`, private key blocks and similar), on credentials embedded in a
URL, and on a secret-named assignment with a long opaque value. Placeholders pass: `${VAR}`,
`<angle>`, `CHANGE_ME`, `var.x`, `secrets.X`. Run it locally with
`bash infra/scripts/check-secrets.sh`. A reviewed false positive carries the comment
`secret-scan: allow` on that line. It is a tripwire for mistakes, not a replacement for review.

## External Secrets Operator plan for S2

S2 moves the control plane to Kapsule (`docs/design/cloud-provider-adr.md`). The VM channel above
does not carry over; the target is External Secrets Operator (ESO) syncing from the provider's
secret manager into Kubernetes `Secret` objects. Nothing of this is built in S1.

- **Store.** Scaleway Secret Manager, because Kapsule and managed PostgreSQL are already
  Scaleway. ESO has a Scaleway provider; confirm the provider's current status at S2 time. Each
  variable in the inventory above becomes one secret entry named after the variable, per environment.
- **Authentication.** ESO authenticates to the secret manager with one scoped IAM API key
  (read secrets in that project), created by hand and installed once as a bootstrap Kubernetes
  `Secret`. It is the only secret applied outside ESO and is documented in the cluster
  runbook, not in a manifest.
- **Objects.** A `ClusterSecretStore` and one `ExternalSecret` per workload that lists the same
  names `check_env` requires today, with `refreshInterval: 1h`, producing a `Secret` the pods
  mount with `envFrom`. The manifests live in `infra/k8s/external-secrets/` and contain
  names and store references only, so the literal-secret check covers them unchanged.
- **Rotation.** Update the entry in the secret manager; ESO syncs it; a reloader controller (or a
  checksum annotation on the deployment) restarts the pods. The per-secret runbooks above stay
  valid, with "edit the GitHub secret and deploy" replaced by "edit the secret-manager entry".
- **Order of work.** Move the secrets that have a zero-downtime path first (R2, provider keys,
  AWS key), then the shared ones (database, RabbitMQ), and the JWT key last, after dual-key
  validation exists.

## Follow-ups

- Dual-key JWT validation (`Jwt__PreviousSigningKey` and the Go equivalent) so a rotation does not
  sign everyone out. Until then the JWT runbook above is the procedure.
- A re-wrap command for `DATA_PROTECTION_KMS_LOCAL_KEY` so self-host can rotate it.
- Separate IAM users for the Inference API (Encrypt and Decrypt) and the engine (Decrypt only).
  Both hosts share one IAM user and its key today.
- Run every rotation section once on staging and record the dates; none has run against real
  infrastructure yet.
- Add the not-yet-built secrets (gateway, Paddle, Turnstile, OAuth) to the inventory and to
  `check_env` as their work packages land.
