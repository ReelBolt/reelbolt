# Contributing to ReelBolt

Thanks for helping. This page covers local setup, tests, and how to send a change.

## Before you start

- For anything larger than a small fix, open an issue first so we can agree on the approach.
- Report security problems privately: see [SECURITY.md](SECURITY.md).
- By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

You can run the full stack with Docker Compose (see the [README](README.md#quickstart-docker-compose)) or work on one component at a time.

| Component | Directory | Needs |
|---|---|---|
| Go API | `api/` | Go (see `api/go.mod`) |
| Inference API, Workflow Engine | `inference/` | .NET 10 SDK |
| Dashboard | `web/` | Node.js and npm |
| Marketing site | `site/` | Node.js and npm |

Full stack:

```bash
cp .env.example .env
cp garage/garage.toml.example garage/garage.toml
# set JWT_SIGNING_KEY, MINIO_SECRET_KEY, SANDBOX_API_TOKEN and IMPORT_WORKER_TOKEN in .env,
# and the three secrets in garage/garage.toml (how to generate each: the guide below)
docker compose up --build -d
docker compose up --build -d <service>   # rebuild one service
```

`docker compose` refuses to run until `JWT_SIGNING_KEY`, `SANDBOX_API_TOKEN` and `IMPORT_WORKER_TOKEN` are set, and the object store needs `garage/garage.toml`. Set a real `MINIO_SECRET_KEY` as well (Garage needs at least 16 characters; compose would otherwise fall back to a placeholder). The [self-hosting guide](docs/user-guide/self-hosting.md#set-the-required-secrets) lists every required secret, how to generate it, and the first sign-in; do not commit `.env` or `garage/garage.toml`.

Go API, from `api/`:

```bash
go run .
go build -o reelbolt .
```

.NET services, from `inference/`:

```bash
dotnet restore
dotnet build ReelBolt.sln
dotnet run --project src/ReelBolt.Inference.Api
dotnet run --project src/ReelBolt.WorkflowEngine
```

Dashboard, from `web/`:

```bash
npm install
npm run dev
```

## Tests and checks

Run the checks for the components you changed before you open a pull request.

```bash
# api/
go test ./...
golangci-lint run

# inference/
dotnet test
dotnet format ReelBolt.sln --verify-no-changes --severity warn

# web/
npm run lint
npm run build
```

Behaviour changes need a test that fails without the change.

## Style

- Go: formatted and linted per `.golangci.yml`.
- .NET: `dotnet format` must report no changes.
- Dashboard: `npm run lint` must pass.
- Documentation: second person, present tense, task first. User-facing docs live in `docs/user-guide/`.
- Never commit secrets or real `.env` files.

## Pull request flow

1. Fork the repository and create a branch from `main`.
2. Make a focused change with a clear commit message.
3. Run the checks above.
4. Open a pull request against `main` and fill in the template.
5. A maintainer reviews it. Continuous integration must pass before merge.

## Contributor License Agreement

ReelBolt is released under the [GNU Affero General Public License v3](LICENSE) and is also offered under a [commercial licence](COMMERCIAL.md). To make that possible, every contributor signs the [Contributor License Agreement](CLA.md) once. You keep your copyright; the agreement gives the project owner the right to use your contribution under both licences.

How it works:

1. Open a pull request as usual.
2. A bot checks whether you have signed. If not, it comments with one sentence to post as a reply: `I have read the CLA Document and I hereby sign the CLA`.
3. The bot records your signature and the check turns green. You never sign again.

Details to know:

- Contributing as part of your job, or your employer may have rights to your work? Ask them to sign the corporate agreement first (see the end of [CLA.md](CLA.md)).
- Commits made by `dependabot[bot]` and `github-actions[bot]` do not need a signature.
- Do not submit code you did not write, or code under a licence that is not compatible with the AGPL-3.0, unless you say so in the pull request and name the source and licence.

For maintainers: signatures are stored on the `cla-signatures` branch by `.github/workflows/cla.yml`. That branch must exist, must not be protected, and the CLA check should be a required status check on `main`.

## Trademark

ReelBolt™ is a trademark of Tommaso Celano. The name and logo are reserved; see [TRADEMARK.md](TRADEMARK.md). Contributing code does not change that.
