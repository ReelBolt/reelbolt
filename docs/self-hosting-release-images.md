# Run ReelBolt from the published images

ReelBolt releases are published as container images on GitHub Container Registry, one per service, named
`ghcr.io/reelbolt/reelbolt-<name>:<version>`. Running from them means nothing is compiled on your machine:
Docker downloads the images and starts them. The current release is `0.4.0-alpha`. Alpha releases have no
`latest` tag, so always name the exact version.

For a from-source install, or for hardening a public install, see
[`user-guide/self-hosting.md`](user-guide/self-hosting.md).

## What you need

- Docker Engine with Docker Compose 2.24 or newer (`docker compose version`).
- About 16 GB of RAM and 40 GB of free disk for the full stack.
- The `docker-compose.yml`, `docker-compose.release.yml`, `.env.example` and the config folders next to them
  (`nginx/`, `garage/`, and so on). Clone the repository at the release tag, or download the release archive
  from GitHub:

  ```bash
  git clone --branch v0.4.0-alpha https://github.com/ReelBolt/reelbolt.git
  cd reelbolt
  ```

- Optional: [`cosign`](https://docs.sigstore.dev/cosign/installation/) to check signatures.

## Configure

```bash
cp .env.example .env
```

Edit `.env`. These values have no default and the stack will not start without them:

| Variable | What to put |
|----------|-------------|
| `REELBOLT_IMAGE_TAG` | The release to run, for example `0.4.0-alpha` |
| `RABBITMQ_PASSWORD` | A long random value |
| `SANDBOX_API_TOKEN` | A long random value (`openssl rand -hex 32`) |
| `IMPORT_WORKER_TOKEN` | A long random value (`openssl rand -hex 32`) |

Also set strong values for `POSTGRES_PASSWORD`, the storage keys and `JWT_SIGNING_KEY`, as described in the
production checklist at the top of `.env.example`. Never commit `.env`.

## Start

Always pass both Compose files. Naming them with `-f` also keeps Compose from loading the developer overrides
file, `docker-compose.override.yml`.

```bash
docker compose -f docker-compose.yml -f docker-compose.release.yml pull
docker compose -f docker-compose.yml -f docker-compose.release.yml up -d
```

The first start downloads several gigabytes. The release file replaces every build in `docker-compose.yml` with
the published image; the supporting services (PostgreSQL, RabbitMQ, object storage, Qdrant and others) use the
same upstream images as always. The sandbox runtime image is pulled by a short one-shot service, and the sandbox
executor starts code from that exact image.

## Check that it works

```bash
docker compose -f docker-compose.yml -f docker-compose.release.yml ps
curl -fsS http://localhost/health
```

Then open `http://localhost/` for the site and `http://localhost/app/` for the app. The documentation is at
`http://localhost/docs/`.

## Verify the images

Every published image is signed with keyless signing from the release workflow. Check one before you run it:

```bash
cosign verify \
  --certificate-identity-regexp '^https://github\.com/ReelBolt/reelbolt/\.github/workflows/image-release\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  ghcr.io/reelbolt/reelbolt-go-api:0.4.0-alpha
```

Replace `go-api` with any image name: `go-api`, `inference-api`, `docs-site`, `workflow-engine`, `web`, `site`,
`mcp`, `sandbox-runtime`, `sandbox-executor`, `runner-gateway`, `worker`, `nginx`, `import-worker`. A successful
check prints the signing identity as JSON; a failure means do not run the image.

## Upgrade and roll back

Both are a change of tag. Back up first (see the self-hosting guide), then:

```bash
# in .env
REELBOLT_IMAGE_TAG=<new version>   # to roll back, set the previous version again

docker compose -f docker-compose.yml -f docker-compose.release.yml pull
docker compose -f docker-compose.yml -f docker-compose.release.yml up -d
```

Database migrations run on start and are not undone by a rollback. If a release changed the schema, restore
the backup you took before upgrading when you go back.

## Licences and source for the GPL parts

The images include GPL-licensed programs, for example FFmpeg and poppler. The written offer for their
corresponding source is in every image at `/licenses/GPL-SOURCE-OFFER.md`:

```bash
docker run --rm --entrypoint cat ghcr.io/reelbolt/reelbolt-sandbox-runtime:0.4.0-alpha /licenses/GPL-SOURCE-OFFER.md
```

The licences of all bundled components are listed in [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md).
