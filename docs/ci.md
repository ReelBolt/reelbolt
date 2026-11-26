# Continuous Integration

CI builds and tests everything on GitHub-hosted runners; a push to `master` also publishes
signed images to GHCR. There is no self-hosted runner pool: every job runs on a runner GitHub
provides (free, with unlimited minutes, for a public repository), so a pull request from a
fork never executes on infrastructure we own.

| Workflow | File | Trigger |
|----------|------|---------|
| CI | [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) | push to `master`, every PR, manual |
| CodeQL | [`.github/workflows/codeql.yml`](../.github/workflows/codeql.yml) | push to `master`, every PR, weekly, manual. C#, Go, JavaScript/TypeScript |
| Secret scan | [`.github/workflows/secret-scan.yml`](../.github/workflows/secret-scan.yml) | push to `master`, every PR, manual. gitleaks over the full history. On a PR the config and ignore file come from the base commit: the tree's `.gitleaksignore`/`.gitleaks.toml` are overwritten with the base copy (or deleted) before the scan, so a PR cannot suppress its own finding. `.github/scripts/secret-scan-trust-test.sh` proves it (plants a fake secret plus a self-serving ignore/allowlist; the scan must still exit 1) and runs as the `trust-test` job |
| Blue-green deploy | `.github/workflows/blue-green-deploy.yml` | manual dispatch only, on `master` in the upstream repository, and only while the repository variable `BLUE_GREEN_DEPLOY_ENABLED` is `true`; the deploy job waits for approval of the `production` environment. Setup in an operator document |
| CLA | [`.github/workflows/cla.yml`](../.github/workflows/cla.yml) | pull requests (contributor license agreement check) |
| Runtime image release | [`.github/workflows/runtime-image-release.yml`](../.github/workflows/runtime-image-release.yml) | `runtime-v*` tag (the desktop runner's sandbox runtime image). Maintainers only; inert in forks |
| Runner release | [`.github/workflows/runner-release.yml`](../.github/workflows/runner-release.yml) | `runner-v*` tag (publishes the desktop runner to the `beta` channel), manual dispatch (a new version, a no-rebuild `promote` of a published beta to `stable`, or a build-and-verify dry run). Maintainers only; inert in forks. Linux only: every job runs on `vars.RUNNER_RELEASE_LABEL`, else `vars.CI_RUNNER_LINUX`, else `ubuntu-24.04` with the toolchains in pinned containers (Windows is cross-built and signed on Linux). Build jobs use no environment and no secret; only the Windows signing job (when publishing) and `publish` use `release`; a dry run uses the secret-free `release-dry-run`. See [`runner/build/README.md`](../runner/build/README.md) |

Maintainers also operate a private deployment workflow that is not part of this repository's
public layout. It is guarded to the upstream repository, so it never runs in a fork. The
blue-green deploy workflow in the table is separate, and is off until enabled.

## Runner model

**Self-hosted notes for `runner-release.yml`.** The workflow needs `docker` on the runner and
nothing else: no `sudo`, no `apt`, no hosted-only tools (the guests in
`.github/self-hosted` have docker, the runner user is in
the `docker` group, and there is no sudo). The Linux toolchain runs in a digest-pinned
`ubuntu:24.04` container (`runner/build/ci`), the Windows one in `runner/build/windows`.
Repository variables: `RUNNER_RELEASE_LABEL` routes the release jobs (register the pool, or a
subset, with a dedicated label that `ci.yml` never references; that is hygiene, not a boundary,
since a PR workflow can name any label, and the ephemeral per-job VM is the isolation), falling
back to `CI_RUNNER_LINUX`; `RUNNER_ARM64_RUNNER` (a runner label) adds a linux-arm64 leg, absent
means none. The Windows signing job needs only curl (OIDC exchange in
`runner/build/windows/azure-oidc-token.sh`), no `az` CLI. There is no shared cache (per-run
tmpfs). Nothing schedules the workflow: if the machine is off, jobs queue until a runner
appears. The workflow has not yet run on GitHub Actions.

- All jobs: `ubuntu-latest` (4 vCPU, 16 GB RAM, about 14 GB free SSD), plus `ubuntu-24.04-arm`
  for the native arm64 smoke render and `windows-latest` for the runner's Windows tests.
- Pull requests use the read-only `GITHUB_TOKEN` and no secrets. `pull_request_target` is not
  used anywhere. Workflows start with `permissions: contents: read`; a job widens its own
  token only where it must (`docker-publish`: `packages: write`, `id-token: write`; CodeQL:
  `security-events: write`).
- Third-party actions are pinned to a full commit SHA with the version in a trailing comment.
  Dependabot (`github-actions` ecosystem, weekly) proposes the bumps.
- Pull requests cancel their own superseded runs; `master` runs are never cancelled, so the
  publish and signing of a commit always finishes.
- The composite action [`.github/actions/free-disk-space`](../.github/actions/free-disk-space/action.yml)
  deletes unused preinstalled toolchains (Android SDK, GHC, CodeQL bundles, ...) at the start of
  the Docker, .NET and compose end-to-end jobs.
- GPU and model-backed work (speech, video and image generation, transcription models) is not
  part of CI and cannot run on hosted runners. Such checks are manual and run on your own
  hardware; a workflow for them would be `workflow_dispatch` only.

## Resource budget

| Job | What it needs on the 16 GB / 14 GB runner |
|-----|--------------------------------------------|
| `docker` / `docker-publish` (12 images, one VM per matrix leg) | 3-5 GB of layers per leg; each leg is a fresh VM and starts with the free-disk step. `sandbox-runtime` also builds `linux/arm64` under QEMU (slowest leg) |
| `dotnet` | the .NET SDK, restore cache and test binaries; low |
| `runner-e2e` | builds the `sandbox-runtime` image locally and runs a throwaway Postgres plus the Go gateway/worker; about 6 GB RAM, 8 GB disk |
| `editor-e2e` | the isolated compose stack (`e2e/scripts/editor-v2-stack.sh`, built from source) plus Chromium. Budget about 8-10 GB RAM and 8-10 GB disk; Playwright runs at most 2 workers (`EDITOR_E2E_WORKERS`). This is a budget, not a measurement: confirm it with the first hosted run and, if the job is OOM-killed or runs out of disk, change it to `workflow_dispatch` only rather than raising the limits |
| `nginx-live`, `infra-lint`, `node`, `go`, `golangci-lint`, `docs-site` | light |

Estimated GitHub-hosted time for a full run (everything triggered, as on a push to `master`):
about 100-140 job-minutes of which roughly 20-30 minutes wall-clock, because the jobs run in
parallel. Pull requests run only the jobs whose paths changed (see "Path filtering"), typically
5-30 job-minutes. These are estimates scaled from earlier runs on a smaller machine; there is
no hosted measurement yet.

## What runs

| Job | Covers | Checks |
|-----|--------|--------|
| `go` (matrix: `api`, `sandbox`, `runner`) | the three Go modules | `go mod tidy` is a no-op, `gofmt`, `go vet`, `go build ./...`, `go test ./... -race` + coverage artifact |
| `runner-windows` | `runner/` | `go vet` and `go test -race` on `windows-latest` (runtime detection is OS-specific). The `go` job's `runner` leg also cross-compiles for `windows/amd64` and `linux/arm64`. |
| `golangci-lint` (matrix: `api`, `sandbox`, `runner`) | the three Go modules | golangci-lint v2.13.2, config in [`.golangci.yml`](../.golangci.yml) |
| `dotnet` | `inference/ReelBolt.sln` (Shared, Inference.Api, WorkflowEngine, WorkflowEngine.Tests) | `dotnet restore`/`build -c Release` (Roslyn + .NET analyzers run here), `dotnet test` with TRX + Cobertura coverage artifacts |
| `dotnet-format` | same solution | `dotnet format --verify-no-changes --severity warn` — blocking, and one of the jobs the `ci` aggregate requires |
| `node` (matrix: `web`, `site`) | both Next.js apps | `npm ci`, `tsc --noEmit`, `npm run lint` (ESLint), `npm test` (Vitest, `web` only), `npm run build` |
| `docs-site` | `docs/` + `docs-site/` | `npm ci`, `npm run build` (Docusaurus over `docs/user-guide` only — fails on any broken link, so it doubles as the user-guide link check; its `postbuild` step, `scripts/assert-user-guide-only.mjs`, fails when the output has a `developer` route or a page from outside the user guide — see [`docs-site.md`](docs-site.md)); it builds every language, then runs `npm run i18n:test` and the non-strict `npm run i18n:stale` (a warning listing stale and untranslated pages) |
| `docker` (matrix: 12 images, incl. `mcp`) | every Dockerfile | buildx build with a GHA layer cache per image, `contents: read` only, never pushes. On a push to `master` in the upstream repository, **and only while the repository variable `PUBLISH_IMAGES` is `true`** (unset by default: nothing is pushed to GHCR until a maintainer sets it, because the images contain GPL ffmpeg and need a source offer first), the sibling job `docker-publish` runs instead: same matrix, and it pushes `ghcr.io/reelbolt/reelbolt-<name>:<sha>` and `:master` with an SBOM and provenance attestation and signs the digest with cosign (keyless, GitHub OIDC). Only `docker-publish` holds `packages: write`/`id-token: write`, so no pull request job can mint the OIDC token the maintainers' deploy workflow verifies. Builds `web`/`site`/`docs-site` at their `production` target and `sandbox` at `sandbox-runtime`, `control-plane`, `runner-gateway` and `worker` (the D13 CloudPool worker, whose image is the only CI-built one that runs customer renders); `inference-api` gets the whole `/docs` tree through a named `build-contexts` entry (`platform-docs=./docs`, for the assistant's documentation search) and `docs-site` only the user guide and its translations (`docs=./docs/user-guide`, `docs_i18n=./docs/user-guide-i18n`; the developer docs are not published). |
| `sandbox-runtime-arm64` | `sandbox/**`, Dockerfiles | Native `ubuntu-24.04-arm` runner: builds `sandbox-runtime` for `linux/arm64` and smoke-renders one second at 320x180 through the stable headless-shell path. The `docker` job's `sandbox-runtime` entry also builds `linux/amd64,linux/arm64` (arm64 under QEMU). |
| `infra-lint` | the plumbing | `infra/scripts/check-secrets.sh` literal-secret tripwire ([`secrets.md`](secrets.md)), actionlint (+ shellcheck on `run:` blocks), ShellCheck on `*.sh`, hadolint on all eight Dockerfiles, `docker compose config` schema check, and static wiring checks under `infra/scripts/tests/` (among them `funnel-env-wiring.test.sh`: the new-user funnel switches and `SITE_FORWARD_TOKEN` reach every service that reads them, nginx included; and `cloudflare-rules.test.sh`: the shape and limits of `infra/cloudflare/rate-limit.*.rules.json`). A change to `.env.example` alone also runs this job |
| `editor-e2e` | `e2e/tests/editor-v2/**` against an isolated local stack, in 4 Playwright shards (`E2E · editor v2 (N/4)`, each builds its own stack, artifacts `editor-e2e-html-report-N` and `editor-e2e-diagnostics-N`; 75 min each, 150 with the WebKit dispatch input) | Runs on every push and manual dispatch, and on a PR only when the editor, its API/render code or the suite changed (paths filter `editor-e2e`). Installs ffmpeg/ffprobe from the pinned static build (`infra/scripts/install-ffmpeg.sh`, no sudo) and Playwright chromium with `--with-deps` starts the stack with `e2e/scripts/editor-v2-stack.sh up` (compose project `reelbolt-editor-remediation`, loopback ports, no LLM or paid provider), then runs `e2e/scripts/editor-v2-picture-storage.sh apply` so presigned storage URLs name a host the runner can reach (the picture upload specs need it), runs `npm run test:editor-v2` on the `desktop` and `pixel7` projects (`iphone14webkit` too when the `editor_webkit` dispatch input is set), uploads the HTML report without traces, then `down -v` and deletes `/tmp/editor-remediation`. Rows that cannot pass yet are listed in `e2e/tests/editor-v2/expected-failures.ts`. Required by the `ci` aggregate |
| `stripe-catalog-sync` | `tools/stripe-catalog-sync/**`, `e2e/stripe-mock/**`, `inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json` | `node --test` in the dependency-free Stripe catalog sync tool, then `node --test e2e/stripe-mock/server.test.mjs` for the e2e Stripe fake (no `npm ci`, no network beyond loopback, no Stripe call). The tool tests cover the promotion-target SQL and the founder window. Path filter `stripe-catalog`: on a PR it runs only when the tool, the fake or the plan catalog they read changed; skipped otherwise, which the `ci` aggregate accepts. See [`tools/stripe-catalog-sync/README.md`](../tools/stripe-catalog-sync/README.md) |
| `ci` | — | Aggregate gate. Point branch protection at this single check rather than the matrix legs |

## Path filtering

The `changes` job (`dorny/paths-filter`) decides which stacks a **pull request**
touched, so a copy tweak under `/site` does not rebuild eight container images.
Push events to `master` ignore the filter and run everything.

A filter also lists files **outside** its own folder when that stack's tests read them. The `api`
filter names four Inference API sources that Go drift tests read and compare with their Go copies:
`inference/src/ReelBolt.Shared/Acquisition/**` and `Services/Telemetry/AttributionSanitizer.cs`
(`api/services/acquisition_vocabulary_drift_test.go`), `Marketing/marketing-consent.wording.json`
(`api/services/marketing_consent_test.go`) and `ReelBolt.Shared/Data/Models/RunnerEnums.cs`
(`api/models/runner_enum_drift_test.go`). Without them a pull request that changed only the C# side
passed with the Go test never run, and `master` turned red after the merge. A new cross-stack drift
test needs its source file added to the reading stack's filter.

`paths-filter` lists the PR's files through the GitHub API, so the `changes`
job carries its own `permissions: { contents: read, pull-requests: read }` on
top of the workflow-wide `contents: read`. That is enough for Dependabot PRs
too: their runs get a read-only `GITHUB_TOKEN` scoped to the permissions the
workflow declares, and a read of the PR's file list is all the job needs.

## Dependabot pull requests

Dependabot PRs run the same workflow as everyone else's and are judged by the
same `ci` check. Two things are worth knowing when one is red:

- **Every job skipped, `Detect changed stacks` failed in under two seconds with
  no steps** — the runner never started. Until October 2026 the repo's
  Dependabot PRs (#138–#154) all failed exactly like this, and the job's
  annotation said why: `The job was not started because recent account
  payments have failed or your spending limit needs to be increased`. That was
  a GitHub billing outage, not a permissions problem; nothing in `ci.yml`
  needed to change. Dependabot PRs opened after billing was restored run and
  pass. Dependabot does not re-run CI on an existing PR by itself — comment
  `@dependabot rebase` on it (or merge `master` into it) to get a fresh run.
- **A `Docker · sandbox-*` job fails on `npm install` with `ERESOLVE`** — the
  bump breaks a peer-dependency contract in `sandbox/template/package.json`,
  which the runtime image installs without a lockfile. The React major is the
  usual culprit: `@react-three/fiber` 9 and `@react-three/drei` 10 need React
  19, and `react-dom` can never move without `react`. `dependabot.yml` groups
  those packages so they are at least proposed together; the bump itself is a
  deliberate wave, since the template is what model-authored Remotion code
  renders against.

Dependabot's own *security* update jobs (the `npm_and_yarn in /docs-site …`
runs in the Actions list) are separate from CI. They fail with
`security_update_not_possible` when the vulnerable package is only reachable
through a dependency Dependabot is not allowed to bump; listing the directory
in `dependabot.yml` so the parent gets regular version updates is the fix.

## Config files

| File | Purpose |
|------|---------|
| [`.golangci.yml`](../.golangci.yml) | Shared by both Go modules — golangci-lint walks up from its working directory to find it. `max-issues-per-linter`/`max-same-issues` are `0` so CI never silently truncates findings |
| [`.hadolint.yaml`](../.hadolint.yaml) | `failure-threshold: warning`. DL3018/DL3008 (apk/apt version pinning) are off by design; reproducibility comes from the base image tag, not from distro package pins |
| [`.github/dependabot.yml`](../.github/dependabot.yml) | Weekly updates for Actions, both Go modules, NuGet, the npm apps (`web`, `site`, `docs-site`, `sandbox/template`), and every Dockerfile base image. Packages that only work as a set are grouped so Dependabot proposes them as one PR: the `Microsoft.Extensions.*`/`Microsoft.Agents.*`/`OpenAI`/`Azure.AI.OpenAI`/`Anthropic` NuGet set, `@mantine/*`, `react`+`react-dom`+their types (in every npm directory), `remotion`+`@remotion/*`, `@react-three/*`, `@docusaurus/*` |

## Running the same checks locally

```bash
# Go — all modules
for m in api sandbox runner; do
  (cd $m && gofmt -l . && go vet ./... && go build ./... && go test ./... -race)
done
golangci-lint run ./...          # from inside api/, sandbox/ or runner/

# .NET
dotnet build inference/ReelBolt.sln -c Release
dotnet test  inference/ReelBolt.sln -c Release --no-build

# Next.js apps
for a in web site; do
  (cd $a && npm ci && npx tsc --noEmit && npm run lint && npm run build)
done

# Stripe catalog sync tool
(cd tools/stripe-catalog-sync && node --test)
node --test e2e/stripe-mock/server.test.mjs

# Infra
actionlint
shellcheck --severity=warning nginx/docker-entrypoint.sh caddy/docker-entrypoint.sh
hadolint --config .hadolint.yaml **/Dockerfile
docker compose config --quiet    # needs JWT_SIGNING_KEY + SANDBOX_API_TOKEN set
```

## Postgres-gated tests

Sixteen test classes in `inference/tests` need a real Postgres. They belong to the xUnit
collection `Postgres` and carry `Trait("Category", "Postgres")`, and they skip unless a connection
string is set in `REELBOLT_TEST_PG` or `REELBOLT_TEST_DATABASE_URL` (an Npgsql connection string).
CI sets neither, so these tests do not run in CI.

To run them, use the script. It starts a throwaway `postgres:16-alpine` container with
`max_connections=200`, runs the tests against it, and removes the container on exit:

```bash
inference/tests/run-postgres-tests.sh
```

Two optional environment variables override the container, for example to run two worktrees in
parallel: `REELBOLT_PGTEST_NAME` sets the container name (default `reelbolt-pgtest-<pid>`), and
`REELBOLT_PGTEST_MAX_CONNECTIONS` sets the server's `max_connections` (default 200):

```bash
REELBOLT_PGTEST_NAME=pgtest-a REELBOLT_PGTEST_MAX_CONNECTIONS=120 inference/tests/run-postgres-tests.sh
```

The test helper `PostgresTestEnvironment` caps every test connection pool at 10, so the collection
stays under the container's `max_connections`. The production pool caps are in
[`database-connections.md`](database-connections.md).

The procedure that ties a version bump to every deploy and public update is an operator document (internal).

## Deploys and running jobs

A deploy must not kill a running workflow. `infra/scripts/release.sh` (the on-host driver for the two-box
deployment, `two-box-deployment.md`) therefore counts the executions in Postgres that are `Queued` or `Running`
before it restarts anything:

- **`deploy` and `rollback` wait, then refuse.** The check runs after the images are built and tagged, right before
  the running stack is touched. It polls every `REELBOLT_DRAIN_POLL` seconds (15) for up to `--drain-timeout`
  / `REELBOLT_DRAIN_TIMEOUT` seconds (1800), logging the count, and then exits non-zero without changing anything,
  listing the executions (short id, status, start time). `--drain-timeout 0` refuses at once. Run it again when the
  jobs are done; the images are already built, so the retry is quick.
- **It fails closed.** If the database cannot be reached or answers something other than a number, the release
  refuses and says why. The control box asks its `postgres` container; the media box (which runs the engine and has no
  database) reads `DATABASE_CONNECTION_STRING` from `.env` and runs `psql` (or a throwaway `postgres:16-alpine`
  container when `psql` is not installed) with the password passed through the environment only. Nothing secret is
  printed.
- **`--force` is the emergency override** (`deploy --force`, `rollback --force`). It counts once, prints which
  executions it is about to interrupt, and goes ahead; if it cannot count it says so and goes ahead anyway. The
  engine re-queues an interrupted run once on its own (`workflow-execution-optimization.md`, "Auto-resume"); anything
  the sweeper cannot resume fails with Resume on offer. `deploy <sha>` re-executes the freshly checked-out script and
  carries `--force` and the timeout through; a stray `REELBOLT_DRAIN_FORCE` in the environment does nothing.
- **Version pre-flight (opt-in).** With `REELBOLT_REQUIRE_VERSION_RAISED=1`, `deploy` and `prepare` first run
  `check-version.sh` (surfaces equal `VERSION`) and `infra/scripts/check-version-raised.sh` (`VERSION` was raised
  past the highest local `v*` tag by semver precedence, or is the tag already at HEAD, as in a re-deploy), and refuse
  before touching the stack otherwise. The caller fetches tags; rollback never checks. Off by default (any other value
  logs that it is off); `REELBOLT_ALLOW_UNRAISED_VERSION=1` overrides a refusal with a loud warning.
- **Known limits.** Runs submitted between the check and the restart are not covered, and new runs keep starting while
  the guard waits (pausing admissions is the platform kill switch, `platform-kill-switches.md`). The GitHub Actions
  deploy (`deploy.yml`, `infra/compose/host.sh`) does not run this guard; only `release.sh` does.

That is the **legacy mode**, which an unadopted box keeps. On a box that has been moved to blue-green slots with
`release.sh adopt --yes`, `deploy` and `rollback` do not wait for or refuse on `Queued`/`Running` runs: the new
release starts in a second slot and the old engine finishes its runs there, up to 8 hours. `--force` then skips
the drain and stops the old slot at once. See an operator document.

The guard is pinned by `infra/scripts/tests/release-path.test.sh` (section 13), run by the `infra-lint` job.

## Expand and contract migrations

Blue-green deploys run the old and the new release against one database for as long as a slot drains, so a
migration may only expand the schema (add nullable or defaulted columns and new tables). Dropping, renaming or
tightening ships in a later release. `MigrationExpandOnlyGuardTests` fails the `dotnet` job for a new migration
that does otherwise; a deliberate contract step carries a `// contract-migration(<CheckName>): <reason>` comment, which exempts only that check. Message
contracts and API-to-engine calls must also tolerate one release of skew. The full rule and a worked example are
in an operator document.

## Known gaps

- **`api/` has no tests.** `go test ./...` passes trivially there (`[no test
  files]`); the gate exists so the first test added is enforced from then on.
  `sandbox/` has `main_test.go` and it passes.
- **No integration tests.** Nothing in CI starts Postgres, RabbitMQ, or the object store (Garage).
  The `dotnet` job runs the WorkflowEngine unit tests (EFCore.InMemory + Moq)
  only; the Postgres-gated tests run by hand (see "Postgres-gated tests").
- **Hosted timings are estimates.** The workflows have been linted with actionlint; the
  resource budget above is confirmed by the first hosted run.

## Protecting the scanner itself (public repository)

On `pull_request`, GitHub runs the PR's own copy of `secret-scan.yml` and `.github/scripts/*`, so a pull request can edit the scanner. Only repository settings close that gap. In the public repository:

1. Add `.github/CODEOWNERS` with the maintainers' handles as owners of `/.github/`, `/.gitleaks.toml`, `/.gitleaksignore`, `**/.gitattributes` and `/tools/open-source-export/`.
2. Create a ruleset on the default branch: require a pull request, require code-owner review, dismiss stale approvals, require the status checks `gitleaks` and `scanner trust-boundary test`, block force pushes and deletion.
3. Under Actions settings, require approval before workflows run for all outside contributors.

The scan reads attributes from git's empty tree (`GIT_ATTR_SOURCE`, git 2.40 or newer), because a PR-supplied `.gitattributes` with `* -diff` would otherwise hide a leak from `git log -p`; the trust test covers that case.
