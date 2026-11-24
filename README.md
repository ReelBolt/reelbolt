# ReelBolt

ReelBolt generates promo videos with [Remotion](https://www.remotion.dev/) through agentic workflows. You upload source code, footage, audio or documents to a project, run a workflow, and agents analyse the material, plan scenes, write and render a video.

ReelBolt is available in two ways:

| | Hosted | Self-hosted |
|---|---|---|
| Where | [reelbolt.ai](https://reelbolt.ai) | Your own machine or server, from this repository |
| Cost | Paid plans | The software is free to run; you pay your own infrastructure and model-provider bills |
| Operations | Managed for you | You run, update and back up the stack |

The source code is open source under the [AGPL-3.0](#licence). Hosting it as a service for others has conditions, so read that section first.

## What is in the repository

- Projects with file upload, folders, automatic text summaries and optional semantic search over text files.
- Workflows made of agent steps, with a starter "promo video from your code" workflow and templates for other jobs.
- Video editing steps built on ffmpeg, plus a timeline editor.
- Configurable model providers, set up by an administrator at runtime.
- Multi-user workspaces with an admin bootstrap account.

See [`docs/user-guide`](docs/user-guide/index.md) for what each of these does from a user's point of view.

## Architecture

```text
nginx :80 -> Go API (auth) | Web dashboard | Inference API -> RabbitMQ -> Workflow Engine -> PostgreSQL | model providers
```

| Directory | Component |
|---|---|
| `api/` | Go REST API: users and JWTs |
| `inference/` | .NET 10 Inference API and Workflow Engine |
| `web/` | Next.js dashboard, served at `/app` |
| `site/` | Next.js marketing site, served at `/` |
| `sandbox/` | Remotion template and render sandbox |
| `docs/user-guide/` | User documentation, published by `docs-site/` |

[`docs/architecture.md`](docs/architecture.md) goes deeper.

## Requirements

- Docker with Docker Compose v2.
- Enough free memory and disk for the full stack, which builds several images and includes local speech and embedding services. The compose file defines what runs; check it before you start on a small machine.
- API keys for the model providers you want to use. These are configured after first login, not required to start the stack.

## Quickstart (Docker Compose)

```bash
git clone https://github.com/ReelBolt/reelbolt.git reelbolt
cd reelbolt
cp .env.example .env
cp garage/garage.toml.example garage/garage.toml
```

Edit `.env` and set these values:

- `JWT_SIGNING_KEY`, `SANDBOX_API_TOKEN` and `IMPORT_WORKER_TOKEN`: required, each at least 32 characters. Generate each with `openssl rand -hex 32`. Compose will not start while any of them is empty.
- `MINIO_SECRET_KEY`: the object store rejects values under 16 characters. Generate one with `openssl rand -hex 20`.
- `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD` and `MINIO_ACCESS_KEY`: the defaults in `.env.example` are placeholders. Change them for anything that is not a throwaway local machine.

In `garage/garage.toml`, replace the `CHANGE_ME` placeholders: `rpc_secret` takes `openssl rand -hex 32`, `admin_token` and `metrics_token` take any long random string. The [self-hosting guide](docs/user-guide/self-hosting.md) has a copy-paste block for all of this, plus first sign-in, hardening and what is optional.

If port 80 or 443 is taken, set `APP_PORT` and `HTTPS_PORT` in `.env`. Plan for about 8 GB of free RAM; the first build takes 10 to 20 minutes.

Then start the stack:

```bash
docker compose up --build -d
```

Open <http://localhost> (the port is `APP_PORT`, default 80). Sign in with `ADMIN_EMAIL` (default `admin@reelbolt.local`). If `ADMIN_PASSWORD` is empty, a random password is printed once to the `go-api` logs on first boot, and you must change it at first login:

```bash
docker compose logs go-api
```

Useful commands:

```bash
docker compose logs -f <service>   # follow one service
docker compose down                # stop the stack
docker compose down -v             # stop and delete all data
```

Public signup is off by default (`SIGNUP_ENABLED=false`); the admin creates users. After signing in, configure a model provider as an administrator: see [inference providers](docs/user-guide/admin/inference-providers.md).

## Configuration

`.env.example` documents every variable. For admin tasks, see `docs/user-guide/admin/`: [providers](docs/user-guide/admin/inference-providers.md), [users](docs/user-guide/admin/users.md), [semantic search](docs/user-guide/admin/semantic-search.md). If something fails, see [troubleshooting](docs/user-guide/troubleshooting.md).

## Licence

ReelBolt is free software under the [GNU Affero General Public License, version 3](https://www.gnu.org/licenses/agpl-3.0.html) (AGPL-3.0). The full text is in the `LICENSE` file of the public repository. You can use, study, change and share it, including for commercial work. If you run a modified version for other people over a network, the AGPL requires you to offer them your modified source.

A **commercial licence** is available if the AGPL does not fit, for example to embed ReelBolt in a closed-source product. Contact **legal@reelbolt.ai**; see [COMMERCIAL.md](COMMERCIAL.md).

Contributions are accepted under a [Contributor License Agreement](CLA.md) that allows the dual licensing above; [CONTRIBUTING.md](CONTRIBUTING.md) explains how signing works. ReelBolt™ is a trademark of Tommaso Celano. The name and logo are not covered by the software licence, AGPL or commercial; see [TRADEMARK.md](TRADEMARK.md).

Copyright (c) 2026 Tommaso Celano.

The AGPL covers ReelBolt's own code. Remotion and the other third-party software below have their own licences.

## Remotion licence

ReelBolt renders its videos with [Remotion](https://www.remotion.dev/). Remotion is source-available software under its own licence; it is not open source, and ReelBolt's licence does not cover it.

- Remotion's Free License covers individuals, for-profit organisations of up to three people (three included), non-profits, and anyone evaluating it. An organisation of more than three people (four or more) needs a Company License. Remotion's "Remotion for Automators" option applies only to Company License users.
- If you run ReelBolt yourself, **you** are the Remotion licensee for that installation. Check which tier applies to your organisation at <https://www.remotion.dev/docs/license> before you run ReelBolt commercially. The hosted service runs under its own Remotion licence; that does not extend to your installation.
- The sandbox container images include Remotion's compositor (an FFmpeg built with GPL options and the non-free libfdk-aac), unmodified, from Remotion's own npm package, version 4.0.542. It is under the Remotion License, not ReelBolt's; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [GPL-SOURCE-OFFER.md](GPL-SOURCE-OFFER.md). Running these images needs your own Remotion licence unless the Free License covers you.
- ReelBolt does not let anyone upload their own Remotion project to be rendered. Remotion's terms prohibit running a service that does.
- Code that ReelBolt's agents write is Remotion code. If you download it or share it, it is subject to Remotion's licence too, and anyone who receives it must check which tier applies to them. Remotion code you export or download must carry a "built with Remotion" notice that links to <https://www.remotion.dev/docs/license>. The notice is about the code, not about the videos it renders.

> Remotion confirmed to the project owner that this use is covered by its free licence. That is the owner's statement, not a published Remotion document, and the Remotion licence does not settle the GPL or libfdk-aac question for the container images (see [GPL-SOURCE-OFFER.md](GPL-SOURCE-OFFER.md)).

## Third-party software

ReelBolt includes and uses third-party software, fonts and models under their own licences. Attributions and licence texts are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Contributing, security and community

- [CONTRIBUTING.md](CONTRIBUTING.md): development setup and pull request flow.
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md): how we expect people to behave.
- [SECURITY.md](SECURITY.md): report vulnerabilities privately.
- [SUPPORT.md](SUPPORT.md): where to ask for help.
