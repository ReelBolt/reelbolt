---
title: Self-hosting ReelBolt
sidebar_position: 12
description: Run your own ReelBolt with Docker Compose - requirements, the secrets you must set, first sign-in, adding a model provider, hardening the install and what is optional.
---

# Self-hosting ReelBolt

This page takes you from a fresh clone to a working ReelBolt on one machine: a dashboard you can sign in to, and a first workflow run that costs nothing. It uses Docker Compose and nothing else.

A self-hosted ReelBolt runs the same codebase as the hosted service at reelbolt.ai, operated by you. Some things exist only on the hosted service: paid plans, credits and billing, and managed operations (see [Plans and credits](account/plans-and-credits.md)). You bring your own model providers and pay them directly. ReelBolt itself does not charge you, and nothing in this guide calls a paid provider.

## What you need

- A Linux, macOS or Windows machine with Docker Engine and the Docker Compose plugin (`docker compose version` should work). Linux is the tested platform.
- **Memory:** plan for about 8 GB of free RAM. The stack idles at roughly 5.5 GB, most of it the local embeddings model (about 3.4 GB); video renders need headroom on top.
- **Disk:** about 15 GB for the images and build cache on first build, plus room for your media.
- **Time:** the first `docker compose up --build` compiles every service and takes 10 to 20 minutes on a fast connection. Later starts take a few minutes.
- Git and `openssl` (to generate secrets).

## Install

```bash
git clone https://github.com/ReelBolt/reelbolt.git reelbolt
cd reelbolt
cp .env.example .env
cp garage/garage.toml.example garage/garage.toml
```

## Set the required secrets

Several services refuse to start, or Compose refuses to run at all, until these have real values. None of them has a safe default, so generate each one yourself and never commit `.env` or `garage/garage.toml` (both are git-ignored).

| Where | Name | What it is |
| --- | --- | --- |
| `.env` | `JWT_SIGNING_KEY` | Signs login sessions. At least 32 characters: `openssl rand -hex 32`. |
| `.env` | `SANDBOX_API_TOKEN` | Shared secret between the workflow engine and the sandbox that renders model-written video code. `openssl rand -hex 32`. |
| `.env` | `IMPORT_WORKER_TOKEN` | Shared secret for the isolated worker that opens uploaded zip and PDF files. `openssl rand -hex 32`. |
| `.env` | `MINIO_SECRET_KEY` | Secret key for the bundled object store (Garage). At least 16 characters: `openssl rand -hex 20`. |
| `garage/garage.toml` | `rpc_secret` | 64 hex characters: `openssl rand -hex 32`. |
| `garage/garage.toml` | `admin_token`, `metrics_token` | Any long random strings: `openssl rand -hex 24`. |

Also change `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD` and `MINIO_ACCESS_KEY` from the example values for anything other than a throwaway local machine. `RABBITMQ_PASSWORD` must be set (Compose refuses to start without it).

You can fill in the generated values with one block (GNU `sed`; on macOS use `sed -i ''`):

```bash
sed -i "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -hex 32)|" .env
sed -i "s|^SANDBOX_API_TOKEN=.*|SANDBOX_API_TOKEN=$(openssl rand -hex 32)|" .env
sed -i "s|^IMPORT_WORKER_TOKEN=.*|IMPORT_WORKER_TOKEN=$(openssl rand -hex 32)|" .env
sed -i "s|^MINIO_SECRET_KEY=.*|MINIO_SECRET_KEY=$(openssl rand -hex 20)|" .env
sed -i "s|CHANGE_ME_32_BYTE_HEX|$(openssl rand -hex 32)|" garage/garage.toml
sed -i "s|admin_token = \"CHANGE_ME\"|admin_token = \"$(openssl rand -hex 24)\"|" garage/garage.toml
sed -i "s|metrics_token = \"CHANGE_ME\"|metrics_token = \"$(openssl rand -hex 24)\"|" garage/garage.toml
```

If port 80 or 443 is already used on the machine, set `APP_PORT` and `HTTPS_PORT` in `.env` to free ports. The rest of this page assumes the default `APP_PORT=80`.

### The admin account

`ADMIN_EMAIL` (default `admin@reelbolt.local`) is the first administrator. Leave `ADMIN_PASSWORD` empty and ReelBolt generates a random password the first time it starts. Set `ADMIN_PASSWORD` instead if you want to choose it yourself.

## Start the stack

```bash
docker compose up --build -d
docker compose ps
```

Wait until the services show `healthy`. `sandbox-runtime` shows `Exited (0)`: it only builds the image the sandbox uses, and that is normal.

## Sign in for the first time

Open <http://localhost> (or `http://localhost:<APP_PORT>`). The dashboard is at `/app`.

If you left `ADMIN_PASSWORD` empty, the generated password is never logged: it is written once to a mode-0600 file on the `go-api-secrets` volume (`/var/lib/reelbolt/bootstrap-admin-password` in the `go-api` container, or the path in `ADMIN_PASSWORD_FILE`), and the `go-api` log names that file:

```bash
docker compose exec go-api cat /var/lib/reelbolt/bootstrap-admin-password
```

Sign in as `ADMIN_EMAIL` with that password. ReelBolt makes you choose a new password before you can do anything else. The file survives container restarts; delete it once you have signed in. If you lose the password before changing it, run `docker compose down -v` and start again on an empty install.

This install has no email provider, so welcome, verification and password-reset emails are written to the `go-api` log instead of being sent. Add one in `.env` (the `SMTP_*` variables) when you invite other people. See [Managing users](admin/users.md).

## Add a model provider

A fresh install has no model provider, so anything that needs an AI model stays unavailable. As the administrator, open **Platform → Inference** and add a provider with your own API key. The steps are in [Inference providers](admin/inference-providers.md). Providers bill you directly; ReelBolt does not see or mark up those costs.

Until you add one, you can still try the editor and the workflows that use no model, such as the green-screen insert and frame extraction. A run of only those steps is counted as a free run.

## Run something free

1. Create a project and upload a short video.
2. Create a workflow with a single **Grab a frame** step pointed at the video, and run it.
3. The run finishes in a few seconds and registers the frame as an image in the project. No provider is called.

## Harden the install

The defaults are meant for a local trial. Before anyone outside your machine can reach it:

- **Switch every component out of development mode.** The shipped `.env` is a developer's `.env`. Before the install is reachable from the internet or a shared network, set these in `.env` and rebuild (`docker compose up --build -d`):
  - `APP_ENV=production` (the sign-in service). In `development` it writes the full sign-in, verification and password-reset links into its log whenever no email provider is set, and anyone who can read that log can use them. In `production` it never does, so configure the `SMTP_*` variables first or invited people will receive nothing.
  - `ASPNETCORE_ENVIRONMENT=Production` (the inference and workflow services). `Development` serves the Swagger API explorer and the OpenAPI document, which list every route.
  - `WEB_BUILD_TARGET=production` and `WEB_NODE_ENV=production` (the dashboard), and `SITE_BUILD_TARGET=production` and `SITE_NODE_ENV=production` (the public site). The defaults build development servers: hot-reload `next dev` with slower, larger pages and verbose errors. For the dashboard also set `WEB_NODE_OPTIONS=` to empty, because the default starts a Node debugger that runs code for anyone who can reach its port.
- **Change the default credentials.** A fresh `.env` uses the well-known database, message-queue and object-store logins (`postgres`/`postgres`, `guest`/`guest`, `minioadmin`). They are acceptable only because those services listen on `127.0.0.1` of a machine that only you use. On any host other people can log in to, or if you ever change those bindings, set strong values for `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `MINIO_ACCESS_KEY` and `MINIO_SECRET_KEY` (`openssl rand -hex 20`) before the first start, and make `JWT_SIGNING_KEY` a fresh random value. Changing them after the first start does not rewrite the stored copies: reset with `docker compose down -v` on an empty install, or change them inside each service.
- **Use real TLS.** Point a domain at the machine, set `DOMAIN` and `ACME_EMAIL` in `.env`, then run `docker compose --profile tls up -d caddy` and `docker compose up -d --force-recreate nginx`. Once `https://<your domain>` shows a trusted certificate, set `FORCE_HTTPS=true` and `COOKIE_SECURE=true` and restart nginx. Leave `COOKIE_SECURE` off over plain `http://`, or sign-in stops working. The same steps, with renewal details, are in `docs/tls.md` in the repository.
- **Keep public sign-up off.** `SIGNUP_ENABLED=false` is the default. The administrator creates accounts.
- **Mind Docker's published ports.** Docker publishes ports around your host firewall (`ufw` and `firewalld` rules do not apply to them). The database, message queue, object store and other internal services are bound to `127.0.0.1` only; keep it that way, and expose only `APP_PORT` and `HTTPS_PORT`. Do not change a `127.0.0.1:` binding to `0.0.0.0` without a reason you can defend.
- **Keep `SANDBOX_NETWORK_EGRESS=false`.** The sandbox runs code written by an AI model. It already has no network access with this setting; do not turn it on.
- **Give the sandbox its own machine for anything shared.** The sandbox service controls Docker on its host through the Docker socket. On a machine that other people use or that holds other data, run it on a dedicated VM and keep its `SANDBOX_API_TOKEN` private.
- **Back up** the Docker volumes (`postgres`, `garage`) and your `.env` file together. Without the secrets in `.env`, the data cannot be read.

## What is optional

The bundled services start by default so that the full feature set works, but you can run without them:

- **Whisper** (local speech-to-text, `whisper`) - only needed for transcription without a cloud provider.
- **Embeddings** (`embeddings`, the largest memory user) - only needed for semantic file search. See [Semantic search](admin/semantic-search.md).
- **ComfyUI, the decision model and Fabric** - GPU and remote-compute features behind the `gpu` and `fabric` Compose profiles. They do not start unless you ask for those profiles.
- **Grafana** - dashboards behind the `observability` profile.

## Remotion licence

ReelBolt renders video with [Remotion](https://www.remotion.dev). Remotion is not open-source software: it is source-available under its own licence, and ReelBolt's licence does not cover it.

- Remotion's Free License covers individuals, for-profit organisations of up to three people (three included), non-profits, and anyone evaluating it. An organisation of more than three people (four or more) needs a Company License. Remotion's "Remotion for Automators" option applies only to Company License users.
- When you self-host ReelBolt you run Remotion yourself, so **you** are the Remotion licensee for that installation. Check which tier applies to your organisation at <https://www.remotion.dev/docs/license> before you use ReelBolt commercially. The hosted service runs under its own Remotion licence; that does not extend to your installation.
- ReelBolt does not let anyone upload their own Remotion project to be rendered. Remotion's terms prohibit running a service that does.
- Code that ReelBolt's agents write is Remotion code. If you download it or share it, it is subject to Remotion's licence too, and anyone who receives it must check which tier applies to them. Remotion code you export or download must carry a "built with Remotion" notice that links to <https://www.remotion.dev/docs/license>. The notice is about the code, not about the videos it renders.

*Remotion confirmed to the project owner that this use is covered by its free licence. That is the owner's statement, not a published Remotion document.*

## If a build fails to download packages

Errors such as `DNS: transient error` from `apk`, or `NU1301` from `dotnet restore`, during `docker compose build` mean Docker's build containers cannot resolve names, which happens when the host's only DNS server is an IPv6 address. Create a build environment with explicit DNS servers and use it:

```bash
printf '[dns]\n  nameservers=["1.1.1.1","8.8.8.8"]\n' > /tmp/buildkitd.toml
docker buildx create --name reelbolt-builder --driver docker-container \
  --driver-opt network=host --buildkitd-config /tmp/buildkitd.toml
BUILDX_BUILDER=reelbolt-builder docker compose build
docker compose up -d
```

For anything else, see [Troubleshooting](troubleshooting.md).
