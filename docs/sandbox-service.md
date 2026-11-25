# Sandbox Service

The sandbox service (`/sandbox`) is a lightweight Go microservice that provides isolated, ephemeral execution environments for Remotion video composition projects. Each environment is backed by a Docker container and a host-mounted workspace directory. The service lifecycle is tied to a workflow execution — one sandbox per `workflowExecutionId`.

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Configuration](#configuration)
- [Sandbox Lifecycle](#sandbox-lifecycle)
- [Janitor / TTL Cleanup](#janitor--ttl-cleanup)
- [Security Model](#security-model)
- [API Reference](#api-reference)
- [Execution Sandbox (Docker Image)](#execution-sandbox-docker-image)
- [Remotion Template](#remotion-template)
- [Docker Build](#docker-build)

---

## Overview

A **sandbox** is a pair of:

1. A **Docker container** spawned from the minimal `reelbolt-sandbox-runtime:local` image (configurable), running as an unprivileged `node` user with a read-only root filesystem, all capabilities dropped, strict resource limits, and — by default — **no network route out at all** (`sandbox-net` is created `--internal`). See [Security Model](#security-model); the code running in that container is assumed hostile.
2. A **host workspace directory** bind-mounted at `/workspace` inside the container, where all source files for the Remotion project live.

The workflow engine (`.NET`) interacts with this service to:

- Create a sandbox when a workflow execution starts.
- Write AI-generated Remotion component files into the workspace.
- Install npm packages required by those components.
- Run `npm run build`, `npx remotion render`, or other allowed scripts inside the container.
- Read back output artefacts (rendered video, build bundles, file listings).
- Delete the sandbox when the workflow is complete or on error.

**This service is the `Local` compute target, and it stays.** `Compute:Mode` defaults to `Local`,
which is the self-host path and the default `docker compose` stack: the engine runs the step here,
in a container on this host, plus in-process ffmpeg. The opt-in **compute fabric**
([compute-fabric.md](compute-fabric.md)) can place a step on a customer's own paired machine or on a
cloud worker instead, but it is off by default, it is not wired to any executor yet, and it does not
replace this service in v1 — retiring `sandbox-executor` in favour of an embedded worker is a
post-launch follow-up.

---

## Architecture

```
┌────────────────────────────────────────────────────────────┐
│  Sandbox Service  (Go HTTP server, :8080)                  │
│                                                            │
│  container.Driver (in-memory registry)                     │
│  ├── sandboxes map[id]*sandbox                             │
│  └── executionIndex map[workflowExecutionId]sandboxId      │
│                                                            │
│  apiHandler → Gorilla Mux routes                          │
│  janitor goroutine (idle stop + TTL cleanup, every 30 s)   │
└──────────────────────────┬─────────────────────────────────┘
                           │  docker CLI subprocess calls
                           ▼
          ┌─────────────────────────────────┐
          │  Docker Engine (host socket)     │
          │                                  │
          │  Container: rf-sbx-<uuid>        │
          │   image: reelbolt-sandbox-      │
          │          runtime:local           │
          │   --network sandbox-net          │
          │        (--internal: no route out)│
          │   --read-only                    │
          │   --user node                    │
          │   -v /var/lib/reelbolt/         │
          │      sandboxes/<uuid>:/workspace │
          └────────────────┬────────────────┘
                           │
                           ▼
             /var/lib/reelbolt/sandboxes/<uuid>/
             (Remotion project source files)
```

The service itself is **stateless on disk** — all runtime state is kept in the container driver's in-memory map. Restarting the service orphans existing containers (they are not recovered). The janitor stops idle containers and cleans up those whose `LastActivity` has exceeded the configured TTL, and never touches one with a command running in it.

---

## Code layout

The module is split so other programs can embed the container driver without the HTTP layer. `sandbox/pkg/container` holds everything that touches containers and workspaces: the `Driver` (create, exec, file access through `os.Root`, janitor, startup adoption), `ValidateExec`/`SanitizeExecRequest`, the npm package-name check and the `--internal` network setup. `sandbox/cmd/sandbox-executor` is the bearer-token HTTP layer on top and builds the `sandbox-executor` binary the `control-plane` image ships. The driver takes a CLI binary (`docker` or `podman`) and an injectable runner, so tests and embedders never need a real container engine.

---

## Configuration

All configuration is read from environment variables at startup. There are no config files.

**Restarts keep sandboxes.** Every sandbox container carries a `reelbolt.execution` label naming its workflow execution. On startup the executor re-adopts each labelled container whose workspace directory under `SANDBOX_ROOT` survived **and whose `reelbolt.config` fingerprint matches the current isolation settings** (the isolation flags, memory/CPU/PID limits, network, `SANDBOX_NETWORK_EGRESS`, and the runtime image's resolved id, so a rebuilt image counts as a change) (stopped, with its original creation time, so the `SANDBOX_TTL` janitor keeps counting from when it was made). Containers without the labels, created under different settings, duplicates and orphaned workspace folders are purged, so a deploy that tightens isolation never keeps an older, looser container alive. Before this, every restart (every deploy) purged all sandboxes, so a finished run's 7-day TTL really ended at the next deploy, and a revision of an older run found nothing to export.

| Variable | Default | Description |
|---|---|---|
| `PORT` | `8080` | HTTP port the service listens on |
| `SANDBOX_API_TOKEN` | *(none — required)* | Shared secret required on every `/api/v1/sandboxes/*` request. The service **exits at startup** if unset |
| `SANDBOX_CONTAINER_CLI` | `docker` | Container CLI the driver shells out to (`docker` or `podman`). The kind is detected at startup with `<cli> version` and logged; both CLIs receive exactly the same flags |
| `SANDBOX_IMAGE` | `reelbolt-sandbox-runtime:local` | Docker image used for each sandbox container. Must be the minimal runtime image, never the control-plane image |
| `SANDBOX_ROOT` | `/var/lib/reelbolt/sandboxes` | Host path where workspace directories are created |
| `SANDBOX_NETWORK` | `sandbox-net` | Docker network sandbox containers attach to. Created by this service, with `--internal` unless egress is enabled. Not declared in `docker-compose.yml` — compose would prefix the name and create a different, unused network |
| `SANDBOX_NETWORK_EGRESS` | `false` | When `false`, the sandbox network is created `--internal`: no internet and no route to the host gateway. When `true`, sandboxed code can reach the network and `POST /packages` is enabled |
| `SANDBOX_TTL` | `168h` (7 days) | Inactivity duration after which a sandbox's container and workspace are removed (e.g. `30m`, `48h`). Separate from the fixed 5-minute idle *stop*; see [Janitor / TTL Cleanup](#janitor--ttl-cleanup) |
| `SANDBOX_EXEC_TIMEOUT` | `5m` | Default execution timeout for `docker exec` calls. Per-request `timeoutSeconds` can override this up to `SANDBOX_EXEC_MAX_TIMEOUT` |
| `SANDBOX_EXEC_MAX_TIMEOUT` | `6h` | Ceiling on a requested exec deadline. The engine sizes a Remotion render's deadline from the composition's frame count and resolution (`RenderTimeoutPolicy`, `Compute:Render:Timeout:*`), so a long film can legitimately ask for hours. On timeout every process the exec started inside the container is killed (each exec carries a `REELBOLT_EXEC_ID` marker), so a timed-out render cannot keep running and starve the next one |
| `SANDBOX_MAX_RENDER_BYTES` | `4294967296` | Largest file `GET /files/raw` will stream (bytes); larger files return 413 |
| `SANDBOX_MEMORY_LIMIT` | `2g` | Docker `--memory` limit per container. A full Remotion render must fit inside it; see [Render memory](#render-memory) |
| `SANDBOX_SHM_SIZE` | `1g` | `--shm-size` (`/dev/shm`) per container; Chrome renders frames through it. Docker's 64 MB default holds fewer than two decoded 4K frames (2160x3840 RGBA is about 33 MB), and full-size renders of 4K or 1440p footage stopped with "could not extract frame" (runs 9bf15ff8, 9e22258a). Counts against `SANDBOX_MEMORY_LIMIT`. `0`/`off` passes no flag. A change re-creates sandboxes (the config fingerprint changes) |
| *(fixed)* `TMPDIR=/workspace/tmp` | — | Every sandbox container runs with `TMPDIR` on the workspace bind mount (host disk), created at init. Chrome writes the frames it fetches from staged clips to its temp folder; on the 256 MB `/tmp` tmpfs that filled up after a few seconds of footage (`FILE_ERROR_NO_SPACE`, Remotion "Failed to fetch ... disk space is low"), so full-size renders with real clips failed (runs 9bf15ff8, 9e22258a, 79a831a1). `tmp` is a top-level name the workspace archive never saves or restores, so it is not captured with a film and survives an import; it is removed with the workspace |
| `SANDBOX_CPU_LIMIT` | `2` | Docker `--cpus` limit per container |
| `SANDBOX_PIDS_LIMIT` | `256` | Docker `--pids-limit` per container |
| `SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES` | `1073741824` (1 GiB) | Largest compressed body `PUT /workspace/archive` accepts. It replaces the 96 MiB request-body cap for that one route only; every other route keeps 96 MiB |
| `SANDBOX_WORKSPACE_MAX_ENTRIES` | `20000` | Most files plus directories a workspace archive may hold, on export and on import |
| `SANDBOX_WORKSPACE_MAX_BYTES` | `2147483648` (2 GiB) | Largest uncompressed total of all files in a workspace archive. On import it is counted on the bytes actually written, not on header claims |
| `SANDBOX_WORKSPACE_MAX_FILE_BYTES` | `536870912` (512 MiB) | Largest single file in a workspace archive |
| `SANDBOX_TEMPLATE_DIR` | `/opt/remotion-template` | The Remotion template on the service's own filesystem (the control-plane image is built `FROM` the runtime image, so the path exists there). `PUT /workspace/archive` compares a restored `package.json` against this template's; a missing template only turns the comparison off |

Duration values accept Go `time.Duration` format strings: `30s`, `5m`, `1h30m`, etc.

### Blue-green slots

During a blue-green deploy two executors can run on one Docker daemon: the old one while its engine drains, and
the new one. Three variables keep them apart (all optional; empty means one executor and the behaviour above):

| Variable | Description |
|---|---|
| `SANDBOX_SLOT` | Slot name of this executor (`blue`, `green`, `g3`, ...). Containers it creates carry the label `reelbolt.sandbox.slot=<slot>`. |
| `SANDBOX_PEER_SLOTS` | Comma list of the slots of the other live executors. Their containers are never stopped, removed or indexed by this executor at startup. |
| `SANDBOX_CONTAINER_PREFIX` | Name prefix for sandbox containers, `[a-z0-9-]+` ending in `-`. Keeps stacks that share one daemon apart (the rehearsal uses it). |

`POST /admin/adopt` (same bearer token as the other routes, `SANDBOX_API_TOKEN`) takes `{"peerSlots": ["<slot>", ...]}` and re-runs the startup adoption with
those peers excluded. the operator release script calls it on the live executor when an old slot is removed, which is the
takeover: the live executor adopts the sandboxes the old one kept. The response is `{"sandboxes": <count>}`.
See the operator runbook.

---

## Sandbox Lifecycle

```
POST /api/v1/sandboxes
   │
   ├─ [idempotent] if a sandbox for this workflowExecutionId already exists → return it (HTTP 200)
   └─ [new] allocate UUID, mkdir workspace, docker run → register in memory (HTTP 201)
          │
          │  (AI agent writes files, installs packages, runs builds)
          │
POST /api/v1/sandboxes/{workflowExecutionId}/complete
   └─ docker stop container; workspace kept on disk, sandbox stays registered (HTTP 200)
          │
          │  (idle past SANDBOX_TTL, 7 days by default)
          │
janitor sweep
   └─ docker rm -f container, rm -rf workspace, deregister from memory
```

`DELETE /api/v1/sandboxes/{workflowExecutionId}` does the same as `/complete`: it **stops** the
container and keeps the workspace. Neither call deletes anything; removal is the janitor's TTL
sweep (see [Janitor / TTL Cleanup](#janitor--ttl-cleanup)). A completed sandbox can therefore still
be read — files, raw renders, `GET /workspace/archive` — until the sweep removes it, and a later
exec or file call restarts its container (an archive export or import never does).

**A service restart wipes every sandbox.** The registry is in memory only, so on startup
`PurgeExisting` removes every `rf-sbx-*` container and every workspace directory under
`SANDBOX_ROOT`. Anything that relies on an old run's sandbox still existing — such as the
engine's fallback of recovering a composition workspace from an earlier execution's sandbox when
no archive was stored — only works until the sandbox-executor next restarts (or the TTL sweep
runs), whichever comes first. A workspace that must outlive that belongs in object storage, via
`GET /workspace/archive`.

### Idempotent Creation

`POST /api/v1/sandboxes` is idempotent: if a sandbox for the given `workflowExecutionId` already exists, it updates `LastActivity` and returns the existing sandbox object with `HTTP 200`. A new sandbox returns `HTTP 201`.

### Container Initialization

On container start, the `sh` entrypoint copies the bundled Remotion template into `/workspace` if `package.json` is not already present:

```sh
if [ ! -f /workspace/package.json ]; then cp -a /opt/remotion-template/. /workspace/; fi; sleep infinity
```

This ensures the workspace is always bootstrapped with a valid Remotion project structure and that the `node_modules` from the pre-installed template are available immediately.

---

## Janitor / TTL Cleanup

A background goroutine ticks every **30 seconds** (less when `SANDBOX_TTL` is very short) and applies two rules to each sandbox, judged on its `LastActivity`:

- idle for more than **5 minutes**: the container is **stopped** (`docker stop`), not removed. The workspace stays on disk and the next call restarts the container.
- idle for more than `SANDBOX_TTL`: the container and workspace are **removed**.

"Last activity" is refreshed when an operation starts and again when it ends: `create`, `exec`, `readFile`, `writeFile`, `listFiles`, `deletePath`, `installPackages`, and the workspace archive export and import. An archive transfer is also counted as in flight for its whole duration, exactly like an exec, so the janitor neither stops nor deletes a sandbox whose workspace is being streamed; unlike an exec it never (re)starts a stopped container, because it only touches the workspace directory on the host.

This means:
- An idle sandbox (e.g. a workflow that crashed mid-execution without calling `/complete`) is eventually stopped and then cleaned up automatically, preventing resource leaks.
- **A sandbox with a command running in it is never stopped or removed**, however long the command takes. The driver counts execs in flight per sandbox, from before the container is (re)started until the command returns, and the janitor skips any sandbox whose count is above zero. Before this, `LastActivity` was only touched when an exec *finished*, so a `remotion render` longer than 5 minutes (a full 1080x1920 render takes 4.5-5.5 minutes on a small box, and a request may ask for up to 15) was stopped mid-frame and died with exit status 137. The idle window now starts when the command ends.

### Stop and restart do not race

`docker stop` can take up to 10 seconds. While it runs, the sandbox carries a *transition* marker, and every path that needs the container running (`create` for an existing execution, every exec and file call) waits for the marker to clear before issuing `docker start`. Starting a container while a stop is still completing used to leave a container that came up and was then killed by the stop, after which every `docker exec` answered "container ... is not running" until the janitor next ran. Concurrent stops of one sandbox also collapse into a single `docker stop`, and the janitor skips a sandbox that is mid-transition.

As defence in depth, if `docker exec` still answers "is not running" (the container died for a reason the driver did not see, such as an OOM kill), the driver marks the sandbox stopped, starts it and retries the command once. That error means the command never started, so the retry cannot run it twice. If the container stays down, `POST /exec` answers `500` with the usual `{"error", "output"}` body, an `error` that says the sandbox was stopped or restarted under the command, that nothing ran, that `/workspace` files are intact and that the command can be retried, and an additional `"code": "SANDBOX_NOT_RUNNING"` field.

---

## Security Model

### Threat model — start here

**Assume arbitrary code is running inside every sandbox container.** This is not a
risk to be mitigated; it is the feature. Agents write TSX and install npm packages,
both of which execute. The `/exec` allowlist does *not* change this: `npm run build`
runs whatever `package.json` says, and `package.json` is a file the caller writes
through `PUT /files/content`. A caller who can reach this API can run any command
inside the container, by design.

Everything below therefore assumes the container is hostile and asks only one
question: *what can it reach?* Two boundaries carry the entire security model —
**the container boundary** and **the network boundary**. The command allowlist and
the package-name regex are input hygiene. They are not containment, and no
security decision should rest on them.

The asset being protected is the **host**. The sandbox service holds
`/var/run/docker.sock`, so code execution in the *service* is equivalent to root on
the host. The service is the crown jewel; the sandbox containers are the untrusted
zone; nothing should ever flow from the second to the first.

### Network boundary

`SANDBOX_NETWORK` (`sandbox-net`) is created **by this service, with `--internal`**,
unless `SANDBOX_NETWORK_EGRESS=true`.

`--internal` is doing the real work. Without it, a Docker bridge gives the container
a default route to the host gateway — and **every port published by `docker
compose` is bound on that gateway**. "Isolated on its own Docker network" is worth
nothing on its own: a sandbox on a normal bridge can reach the host's published
Postgres, RabbitMQ, the object store, and nginx just by addressing the gateway IP. With
`--internal` there is no default route at all: no internet, no other network, no
host.

Reinforcing that, `docker-compose.yml` publishes every port except nginx's on
`127.0.0.1` only, so nothing but the reverse proxy is reachable from off-host or
from a container that somehow acquires a route.

The service **refuses to start** if a network of that name already exists without
`Internal: true` — otherwise a leftover network from an older release would
silently restore egress.

**Enabling egress** (`SANDBOX_NETWORK_EGRESS=true`) is what makes
`POST /packages` work, because npm needs the registry. It also gives
attacker-controlled code a network. Prefer adding dependencies to the runtime
image; with egress off, `POST /packages` returns `409 Conflict` explaining this
rather than hanging until the exec timeout.

### Container boundary

| Flag | Effect |
|---|---|
| `--network sandbox-net` (`--internal`) | No default route: no internet, no host gateway, no other Docker network. See above |
| `--read-only` | Root filesystem is read-only — only `/workspace` and the tmpfs mounts are writable |
| `--tmpfs /tmp:rw,nosuid,nodev,size=256m` | Ephemeral `/tmp` limited to 256 MB, no setuid, no device nodes |
| `--memory` | Hard memory cap (default 2 GB) |
| `--cpus` | CPU share limit (default 2 cores) |
| `--pids-limit` | Max OS processes (default 256), preventing fork bombs |
| `--security-opt no-new-privileges` | Prevents privilege escalation via `setuid` binaries |
| `--cap-drop ALL` | Drops all Linux capabilities |
| `--user node` | Runs as the unprivileged `node` user, not root |

Note what this list does **not** include: user-namespace remapping and a custom
seccomp/AppArmor profile. A kernel-level container escape is therefore still an
escape to the host. Sandbox containers are hardened, not virtualised; if you need a
hard boundary against a kernel exploit, run the Docker host in a dedicated VM.

### Two images, not one

The image untrusted code runs in (`reelbolt-sandbox-runtime:local`, the
`sandbox-runtime` build target) is **not** the image the service runs as
(`reelbolt-sandbox-executor:local`, the `control-plane` target). These were the
same image previously, which meant every sandbox shipped with the Docker CLI,
`curl`, `wget`, `gnupg`, and the service binary. A Docker client inside the
untrusted container earns nothing for rendering and turns any reachable daemon
endpoint into instant host root.

Keep the runtime target minimal. Anything added to it is added to the attacker's
toolkit.

### API authentication

Every `/api/v1/sandboxes/*` route requires `Authorization: Bearer $SANDBOX_API_TOKEN`,
compared in constant time. `SANDBOX_API_TOKEN` has **no default** and the service
exits at startup if it is unset.

This API is **never exposed through nginx**. Its only client is the workflow engine,
in-cluster over the `reelbolt` network. Do not add an nginx `location` for it: it
was previously proxied at `/api/v1/sandboxes` with no auth of any kind, which made
"write a `package.json`, then `POST /exec`" an unauthenticated remote code execution
path from the public internet into the container holding the Docker socket.

`/health` stays unauthenticated for container healthchecks and returns no state.

### Path traversal and symlinks

All file operations go through **`os.Root`** rooted at the workspace, which enforces
containment per path component at the syscall level and refuses any traversal that
leaves the root — including through a symlink.

A lexical check (`filepath.Clean` plus a prefix comparison) is *not* sufficient here
and was the previous implementation's flaw. Code inside the sandbox can create
`/workspace/escape -> /`; the service then resolves that link **in its own mount
namespace**, which is where the Docker socket is mounted. That turned "arbitrary
code in the sandbox" into arbitrary read/write in the control plane, and from there
into root on the host. `removeAllIn` likewise uses `Lstat`, so deleting a symlink
unlinks the link and never recurses into its target.

The workspace root itself cannot be written or deleted through the API.

### Workspace archives

`GET` and `PUT /api/v1/sandboxes/{id}/workspace/archive` move a whole workspace out of a sandbox
and into another one (composition revisions: the engine snapshots the Remotion project after a run
and restores it into a fresh sandbox for a later edit). The workspace was written by hostile code,
and an uploaded archive is treated as hostile input, so the rules are:

- **`os.Root` both ways.** The export walks the workspace through the root (`Open` + `ReadDir` +
  `Lstat`), never `filepath.Walk` on a host path, and checks each directory it descends into is the
  object it `Lstat`ed, so a directory swapped for a link mid-walk is not followed. The import writes
  only through the root; a symlink (or FIFO, or device) already sitting at a path the archive
  writes — e.g. `src -> /` planted by sandbox code — is unlinked and replaced by a real directory or
  file rather than written through.
- **Never block on a FIFO.** Every workspace path the archive code opens — each file it copies,
  each directory it lists, a restored or template `package.json` (read with a 1 MiB cap), a file it
  overwrites — is opened `O_NONBLOCK`, `fstat`ed, and refused unless it is a regular file (or a
  directory where one is expected) and the same object the earlier `Lstat` measured; only then is
  blocking mode restored. A FIFO planted by sandbox code would otherwise hang the control plane's
  goroutine on `open` forever. `removeAllIn` (used by `DELETE /files` and `replace=true`) applies
  the same check to every directory it descends into.
- **No links, no special files.** Only regular files and directories are archived; symlinks,
  devices, FIFOs and sockets are skipped (and counted in `X-Workspace-Skipped`). On import, every
  entry type other than a regular file or directory — symlink, hard link, character or block
  device, FIFO, a stray PAX global header — is refused, as is any name that is absolute, contains a
  `..` component, a backslash or a NUL byte, or cleans to the workspace root. A refused entry fails
  the whole import with `400` `ARCHIVE_INVALID`.
- **Exclusions.** `node_modules` at any depth, and the top-level `out`, `build`, `dist`, `.cache`,
  `.remotion`, `.git`, `.npm` and `tmp`, are never archived, and an archive that contains them is
  refused. `replace=true` keeps those excluded entries, which is how a restore lands on top of the
  template's `node_modules` that the new container copied in.
- **Caps before trust.** Entry count, per-file size and total size are enforced on export *before
  the first byte is sent*, so an oversized workspace is a `413` with a JSON body rather than a
  truncated download. On import the compressed body is capped (`SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES`)
  and the uncompressed total is counted on the bytes actually written, never on header claims, so a
  gzip bomb stops at the cap.
- **Deterministic output.** Entries are sorted by path, with fixed mtime (epoch), uid/gid 0, no
  user/group names, and mode `0644`/`0755`; the gzip header carries no name or time. Two exports of
  an unchanged workspace are byte-identical, which the engine relies on to deduplicate stored
  versions by SHA-256.
- **Streams, not base64.** A workspace with assets can run to hundreds of megabytes; base64 inside
  JSON would inflate it by a third and force both sides to buffer it whole. The archive is a raw
  `application/gzip` body in both directions, streamed straight between this service and the object
  store by the engine.

A failed import leaves the workspace partially restored (with `replace=true`, its previous source
tree already removed). That is accepted: the engine treats it as a failed restore, and the sandbox
is disposable. A restored file is chowned to the sandbox's `node` user when the service has the
privilege to do so.

### Input hygiene (not containment)

The `/exec` allowlist (`npm run <build|render|typecheck|compositions>`,
`npx remotion <render|still|compositions>`, and `node` running only the shipped
layout-lint script) and the package-name regex

```
^(@[a-z0-9][a-z0-9\-_.]*/)?[a-z0-9][a-z0-9\-_.]*(@[a-zA-Z0-9.\-_]+)?$
```

keep honest callers on the intended path and stop malformed input reaching a
subprocess. The regex requires an alphanumeric first character specifically so a
"package" named `--foo` cannot be spliced into `npm install --save` as a flag; the
install command additionally passes `--` before the package list.

Neither mechanism constrains what ultimately executes inside the container. Treat
them as validation, never as a security boundary.

---

## API Reference

All endpoints are prefixed with `/api/v1/sandboxes`. The `{workflowExecutionId}` path parameter must be a valid UUID v4.

**Every endpoint below requires `Authorization: Bearer $SANDBOX_API_TOKEN`** and
returns `401 Unauthorized` without it. `GET /health` is the only unauthenticated
route. None of these are reachable through nginx — this API is in-cluster only.

### Health Check

```
GET /health
```

Response `200 OK`:
```json
{ "status": "ok" }
```

---

### Create Sandbox

```
POST /api/v1/sandboxes
Content-Type: application/json

{
  "workflowExecutionId": "550e8400-e29b-41d4-a716-446655440000"
}
```

- Returns `201 Created` for a new sandbox, `200 OK` if one already exists for the given execution ID.

Response body:
```json
{
  "id": "7d6e2f1a-...",
  "workflowExecutionId": "550e8400-...",
  "containerName": "rf-sbx-7d6e2f1a-...",
  "workspacePath": "/var/lib/reelbolt/sandboxes/7d6e2f1a-...",
  "createdAt": "2026-03-07T10:00:00Z",
  "lastActivity": "2026-03-07T10:00:00Z"
}
```

---

### List Sandboxes

```
GET /api/v1/sandboxes
```

Response `200 OK`: array of sandbox objects (same schema as above).

---

### Get Sandbox

```
GET /api/v1/sandboxes/{workflowExecutionId}
```

Response `200 OK`: single sandbox object.  
Response `404 Not Found` if not found.

---

### Delete Sandbox

```
DELETE /api/v1/sandboxes/{workflowExecutionId}
```

Stops the container (`docker stop`) and keeps the workspace and the registration, exactly like `/complete`. Nothing is deleted here: the janitor removes the container and workspace once the sandbox has been idle for `SANDBOX_TTL` (7 days by default), and a service restart removes every sandbox. See [Sandbox Lifecycle](#sandbox-lifecycle).

Response `200 OK`:
```json
{ "ok": true }
```

---

### Get Sandbox Status

```
GET /api/v1/sandboxes/{workflowExecutionId}/status
```

Returns readiness information without requiring the sandbox to exist.

Response `200 OK` (sandbox not found):
```json
{ "exists": false }
```

Response `200 OK` (sandbox found):
```json
{
  "exists": true,
  "ready": true,
  "hasPackageJson": true,
  "hasNodeModules": true,
  "containerName": "rf-sbx-7d6e2f1a-...",
  "workspacePath": "/var/lib/reelbolt/sandboxes/7d6e2f1a-...",
  "createdAt": "2026-03-07T10:00:00Z",
  "lastActivity": "2026-03-07T10:02:30Z"
}
```

`ready` is `true` when both `package.json` and `node_modules/` exist in the workspace — meaning the project can be built or rendered immediately.

---

### Complete Workflow (Stop Sandbox)

```
POST /api/v1/sandboxes/{workflowExecutionId}/complete
```

Called by the workflow engine when an execution finishes (success or failure). It **stops** the container and keeps the workspace on disk; the sandbox stays registered, so its files (and `GET /workspace/archive`) remain readable until the janitor removes it after `SANDBOX_TTL` of inactivity (7 days by default) or the service restarts. Functionally identical to `DELETE /api/v1/sandboxes/{workflowExecutionId}`.

Response `200 OK`:
```json
{ "ok": true }
```

---

### Execute Command

```
POST /api/v1/sandboxes/{workflowExecutionId}/exec
Content-Type: application/json

{
  "command": "npm",
  "args": ["run", "build"],
  "timeoutSeconds": 120
}
```

Runs the given command inside the sandbox container via `docker exec`. `timeoutSeconds` is optional; defaults to `SANDBOX_EXEC_TIMEOUT`. Maximum is `SANDBOX_EXEC_MAX_TIMEOUT` (6 hours by default). A timed-out command is killed inside the container, not just detached.

**Allowed commands:**

| `command` | `args[0]` | `args[1]` |
|---|---|---|
| `npm` | `run` | `build` \| `render` \| `typecheck` \| `compositions` (trailing args only after one `--` separator element, each `--flag` or `--flag=value`; npm config flags before it are refused) |
| `npx` | `remotion` | `render` \| `still` \| `compositions` |
| `node` | `/opt/remotion-template/reelbolt/check-layout.mjs` | `--name=value` flags only (at most 8; value `[A-Za-z0-9_.,-]`) |

`node` runs exactly one first-party script, the layout lint behind the engine's `CheckLayout` tool
(see [Layout lint](#layout-lint)). Its path is the image's copy of the template, not the workspace's,
so a workspace that deletes or rewrites `reelbolt/` still gets the shipped script. No other script,
no `-e`/`--eval`, and no positional argument is accepted.

Response `200 OK`:
```json
{ "output": "...(stdout+stderr combined)..." }
```

Response `400 Bad Request` (command not allowed):
```json
{ "error": "command not allowed", "output": "" }
```

Response `500 Internal Server Error` (execution failure):
```json
{ "error": "execution failed: exit status 1", "output": "...(stdout+stderr)..." }
```

When the sandbox container was down and could not be brought back, the same shape carries a self-explanatory `error` (the sandbox was stopped or restarted under the command, nothing ran, retry) and an extra `"code": "SANDBOX_NOT_RUNNING"`. See [Janitor / TTL Cleanup](#janitor--ttl-cleanup).

---

### Install npm Packages

```
POST /api/v1/sandboxes/{workflowExecutionId}/packages
Content-Type: application/json

{
  "packages": ["framer-motion", "@remotion/shapes@4.0.0"]
}
```

Runs `npm install --save -- <packages...>` inside the sandbox container. Each package name is validated against the allowlist regex before execution.

Response `200 OK`:
```json
{ "output": "...(npm install output)..." }
```

Response `400 Bad Request` if any package name is invalid.

Response `409 Conflict` when `SANDBOX_NETWORK_EGRESS=false` (the default): the
sandbox network has no route to the registry, so the install cannot succeed. Bake
the dependency into the runtime image instead of enabling egress where possible.

---

### List Files

```
GET /api/v1/sandboxes/{workflowExecutionId}/files?path=src
```

Lists directory contents at the given relative path within the workspace. `path` defaults to the workspace root if omitted.

Response `200 OK`:
```json
[
  { "name": "index.ts",    "isDir": false, "size": 82,   "modTime": "2026-03-07T10:01:00Z" },
  { "name": "root.tsx",    "isDir": false, "size": 654,  "modTime": "2026-03-07T10:01:00Z" },
  { "name": "components",  "isDir": true,  "size": 4096, "modTime": "2026-03-07T10:02:00Z" }
]
```

---

### Get File Content

```
GET /api/v1/sandboxes/{workflowExecutionId}/files/content?path=src/root.tsx
```

Reads a file and returns its content Base64-encoded. `path` is required. Files larger than 64 MiB are refused with `413 Payload Too Large` (they used to be silently truncated); use the streaming endpoint below for large files such as renders.

Response `200 OK`:
```json
{
  "path": "src/root.tsx",
  "contentBase64": "aW1wb3J0IHR5cGUgUmVhY3QuLi4="
}
```

---

### Get Raw File (streaming)

```
GET /api/v1/sandboxes/{workflowExecutionId}/files/raw?path=out/video.mp4
```

Streams a regular file as `application/octet-stream` with an exact `Content-Length` (no Base64, no buffering). Same bearer-token auth and `os.Root` workspace containment as `/files/content`; directories, devices and symlinks leaving the workspace are refused. Files larger than `SANDBOX_MAX_RENDER_BYTES` (default 4 GiB) get `413`. The workflow engine uses it to move a rendered video straight into the object store.

---

### Write File Content

```
PUT /api/v1/sandboxes/{workflowExecutionId}/files/content?path=src/components/Hero.tsx
Content-Type: application/json

{
  "contentBase64": "aW1wb3J0IHR5cGUgUmVhY3QuLi4="
}
```

Creates or overwrites a file at the given relative path. Parent directories are created automatically. Content must be Base64-encoded. `path` is required.

Response `200 OK`:
```json
{ "ok": true }
```

---

### Delete File or Directory

```
DELETE /api/v1/sandboxes/{workflowExecutionId}/files?path=src/old-component.tsx
```

Deletes a file or directory (recursively) at the given path. `path` is required. Deleting the workspace root is not allowed.

Response `200 OK`:
```json
{ "ok": true }
```

---

### Export Workspace Archive

```
GET /api/v1/sandboxes/{workflowExecutionId}/workspace/archive
```

Streams the whole workspace as a gzip-compressed tar, for the engine to store as a composition
revision. Regular files and directories only (symlinks, devices, FIFOs and sockets are skipped and
counted); `node_modules` at any depth and the top-level `out`, `build`, `dist`, `.cache`,
`.remotion`, `.git`, `.npm` and `tmp` are left out. The output is deterministic — two exports of an
unchanged workspace are byte-identical. The container is not started; a stopped sandbox exports
just the same. See [Workspace archives](#workspace-archives) for the handling rules.

Response `200 OK`: `Content-Type: application/gzip`, streamed with no `Content-Length`, plus

| Header | Meaning |
|---|---|
| `X-Workspace-Entries` | Files plus directories in the archive |
| `X-Workspace-Bytes` | Uncompressed size of the archived files |
| `X-Workspace-Skipped` | Entries left out because they are not regular files or directories |

Every cap is checked before the first byte is sent, so errors carry the usual JSON body:
`400` for an invalid execution id, `404` for an unknown sandbox, and `413` when the workspace
exceeds `SANDBOX_WORKSPACE_MAX_ENTRIES`, `SANDBOX_WORKSPACE_MAX_BYTES` or
`SANDBOX_WORKSPACE_MAX_FILE_BYTES`:

```json
{ "error": "workspace archive too large: workspace has more than 20000 entries", "code": "ARCHIVE_TOO_LARGE" }
```

A failure after streaming began (the workspace changed under the export, a disk error) resets the
connection. The gzip trailer is never written in that case, so a client always sees a truncated or
invalid stream, never a short archive that decodes cleanly.

---

### Import Workspace Archive

```
PUT /api/v1/sandboxes/{workflowExecutionId}/workspace/archive?replace=true
Content-Type: application/gzip

<raw tar.gz bytes, as produced by GET /workspace/archive>
```

Restores an archive into the workspace. `replace` defaults to `true`: every top-level entry except
the excluded ones is removed first, so the result is the archive on top of the template's
`node_modules`; `replace=false` merges over what is there. The body may be up to
`SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES` (1 GiB by default) — this route alone is exempt from the
96 MiB request-body cap. The container is not started.

Because the sandbox network has no egress, a restored project can only build with dependencies the
runtime image already ships, so the response compares the restored `package.json` against the
template's (`SANDBOX_TEMPLATE_DIR`).

Response `200 OK`:
```json
{
  "files": 42,
  "directories": 9,
  "bytes": 183920,
  "templateCompared": true,
  "dependenciesMatchTemplate": false,
  "extraDependencies": ["three@0.170.0"]
}
```

| Field | Meaning |
|---|---|
| `files`, `directories` | Entries written |
| `bytes` | Uncompressed bytes written |
| `templateCompared` | `false` when either manifest is missing, not a regular file or not valid JSON |
| `dependenciesMatchTemplate` | `templateCompared` and no extra dependencies |
| `extraDependencies` | Sorted `name@spec` for every `dependencies`/`devDependencies` entry the template lacks or declares with a different spec. Always an array, never `null` |

Errors (JSON body):

| Status | `code` | When |
|---|---|---|
| `400` | `ARCHIVE_INVALID` | Not gzip/tar, or an entry that is a link, device or FIFO, an absolute or `..` path, a backslash or NUL in a name, or a path under an excluded directory |
| `400` | — | Invalid execution id, or `replace` is not a boolean |
| `404` | — | Unknown sandbox |
| `413` | `ARCHIVE_TOO_LARGE` | Body over `SANDBOX_MAX_WORKSPACE_ARCHIVE_BYTES`, or content over the entry, per-file or total caps |

A failed import may leave the workspace partially restored; treat it as a failed restore.

---

## Execution Sandbox (Docker Image)

The runtime container image (`reelbolt-sandbox-executor:local`) must be pre-built and available on the Docker host. It is **not** built by the sandbox service itself. The image must provide:

- Node.js 22 + npm
- Chromium (for Remotion's headless renderer)
- ffmpeg (for video encoding)
- The Remotion template pre-installed at `/opt/remotion-template/` (including `node_modules`)
- A non-root `node` user

The service's own `Dockerfile` (see below) also builds this image as part of a multi-stage build for convenience in local development and CI.

---

## Remotion Template

The `/sandbox/template/` directory contains the bootstrapped Remotion project that is copied into every new sandbox workspace. It is baked into the runtime container image at `/opt/remotion-template`.

### Render memory

Left to its defaults, Remotion 4 sizes itself from the host, not the container: one browser tab
per half of `os.cpus()` and an `<OffthreadVideo>` frame cache of half the memory free when the
render starts. With several staged videos playing at once that overran a 4 GB sandbox (run
571c3adf: `Memory cgroup out of memory: Killed process (remotion)`, then `Compositor quit with
signal SIGKILL` at frame 352, exit 1).

So the engine's `RenderVideoAndUploadToStorage` adds two flags to every render whose args do not
already set them (both exist in the pinned Remotion 4.0.545):

| Flag | Engine key | Default |
|---|---|---|
| `--concurrency=<n>` | `Compute:Render:DefaultConcurrency` | `2` (the default `SANDBOX_CPU_LIMIT`, so no slower there) |
| `--offthreadvideo-cache-size-in-bytes=<b>` | `Compute:Render:OffthreadVideoCacheBytes` (floor) and `Compute:Render:OffthreadVideoCacheMaxBytes` (ceiling) | sized from the staged videos, at least `268435456` (256 MiB), at most `1073741824` (1 GiB) |

The video cache is sized from what is actually staged (see [Oversized staged videos](#oversized-staged-videos)):
12 decoded frames of every video under `public/`, in every render tab, at 4 bytes a pixel, never
below the configured default and never above the ceiling. A video with no record (the restore pass
below could not handle it) counts as a 1920x1080 frame, or as a 3840x2160 frame when its file is over
32 MiB: no cheap probe exists inside the sandbox, and a phone's 4K clip passes 32 MiB in about six
seconds while a 1080p H.264 clip takes about half a minute, so the guess errs towards a larger cache. With no video staged it is the default. Remotion
splits the cache between its tabs, and each tab needs frames of every clip on screen: a flat 256 MiB
gave five 4K clips about one frame each per tab, so every frame was decoded again from the last
keyframe and the render stalled.

A value the model passes is kept. The engine does not read `SANDBOX_MEMORY_LIMIT`; the 1 GiB ceiling
suits the 8 GB production sandbox. Lower the ceiling or the concurrency if you shrink the limit
(the default 2 GB self-host sandbox relies on the out-of-memory retry below when many videos play).

If a full render still fails with a kill marker in its output (`SIGKILL`, `Killed`, `ENOMEM`,
`out of memory`, `constraint_memcg`) before its deadline, the tool retries it once, in the same call, with
`--concurrency=1` and half the cache (at least 32 MiB), under twice the deadline (still capped by
`Compute:Render:Timeout:MaxSeconds`). The retry is logged as a warning. It is not a second render
for the render-once guard, and the call records at most one failure on the failed-render cap
(`Compute:Render:MaxFailedRendersPerStep`). If the retry is killed too, the tool answers
`outOfMemory: true` with guidance to have fewer videos decoding at once and to use shorter or
lower-resolution sources, and to keep the real footage. A kill that arrives once the render has run for 95% or more
of its deadline is the executor stopping it on time (`execution failed: signal: killed` looks the same
as an out-of-memory kill): it is not retried and the tool answers `timedOut: true` with a time-limit
message, not the memory one. Raw renders through
`RunSandboxRemotionCommand` get neither the defaults nor the retry.

### Oversized staged videos

Runs 36df4287 and 9bf15ff8 failed at the render with "Could not extract frame from compositor": the
Author played five 2160x3840 clips at once (a 2x3 grid in a 1080x1920, 30 fps composition; 23.976,
25, 30 and 59.94 fps sources, one full-range `yuvj420p`). Reproduced on 2026-11-05 with the same six
files, Remotion 4.0.542, `--concurrency=2`: with the 256 MiB cache the compositor kept about five 4K
frames per tab, each frame took seconds, and the render died at frame 25 on a `delayRender()`
timeout for `/proxy?src=...ad-latte-art-topdown.mp4`; the originals also failed with a 2 GiB cache.
1080x1920 copies failed at 256 MiB and rendered at 512 MiB and 1 GiB, every frame of the 30 fps cells
matching its source frame (PSNR 39 dB or more on all 240 frames; a one-frame shift drops to 21 dB).

So two things happen without anyone re-encoding footage:

- **Staging an oversized video stages a delivery copy.** `StageProjectFile` and `StageGeneratedClips`
  download the video to engine scratch and probe it (ffprobe under the derivative demuxer whitelist).
  When its long side is over `Compute:Render:StagedVideoMaxLongSide` (default `1920`; raised to the
  16:9 long side of the run's chosen output resolution, so a 2160p run keeps 3840) the ENGINE's ffmpeg
  makes a copy (`StagedVideoProxy`): lanczos down to fit, the same frames at the same times
  (`-fps_mode passthrough`, never a new frame rate), 8-bit 4:2:0 BT.709 limited range (full range is
  converted; HLG/PQ is tone-mapped like the timeline render when this host can), H.264 CRF 18 with a
  one-second GOP, AAC stereo, `+faststart`. The copy is verified (it must probe, at the source's frame
  rate) and staged under the SAME path, so the composition's `staticFile()` does not change. The tool
  result gains `video: {width, height, fps, proxied, sourceWidth, sourceHeight, stagedSizeBytes,
  note}`; a video staged as it is reports `proxied: false` and its size. Only a destination named
  `.mp4`, `.m4v` or `.mov` gets a copy (the bytes are MP4), and a transparent video never does.
- **Cached by the source object.** Copies and the facts of every probed video live at
  `projects/{projectId}/agentFiles/render-proxies/{sha256}.mp4|.json`, keyed on the recipe version,
  the source key, its size, its ETag and the long side; staging the same object again downloads and
  probes nothing. They count as project storage and go with the project.

Every staged video's facts are also written to `.reelbolt/staged-videos.json` in the workspace (outside
`public/`, never served); the render's cache sizing and the failure message below read it. Anything
that goes wrong in the copy path (probe, encode, verification, upload, a missing cached copy) stages
the original exactly as before, and the copy never fails a staging. ffmpeg never runs in the sandbox
(see "Why ffmpeg runs in the WorkflowEngine container" in CLAUDE.md). The encode's time does not count
against the agent's run budget; `VideoEditing:CompileTimeoutSeconds` bounds it. A composition rendered
at 4K with the output resolution left on Auto plays the 1920 copies scaled up; choose 2160p for the
run to keep 4K footage. Mixed frame rates (a 25 fps clip in a 30 fps composition) are left as
Remotion plays them.

Each record also carries `sourceKey` (the object it was staged from) and `reason` (why it was or was
not copied: `oversized`, `within_size`, `has_alpha`, `container_not_mp4_family`, `no_video_stream`,
`copy_failed`, `copy_unverified`, `copy_not_staged`).

**Restored workspaces get the same treatment.** Run 9e22258a (a composition revision) stopped at frame
93, "could not read a frame from public/ad-pour-warm-light.mp4" (2160x3840): the workspace it restored
was captured before the copies existed, so `public/` held the originals and no record, and the reviser
edits code without staging every clip again. After every restore (`CompositionWorkspaceRestorer`, so
revisions and their change-request replays, continue-composition, the graphic designer and a recovered
sandbox alike), and again before a full render for anything still unrecorded, every `.mp4`/`.m4v`/`.mov`
under `public/` with no record, or with a record of an oversized original that could be copied, is
handled like a fresh staging (`ReactRemotionSandboxTools.RestoredVideos.cs`):

- **Mapped when possible.** Its recorded `sourceKey`, when that is still one of the project's listed
  files, else the ONE project file with the same name and size, goes through the staging path itself:
  the cached copy (same recipe, same cache key) is staged under the same name without reading the
  sandbox, or the object is probed and copied once in the engine. A record can never name an object
  that is not a listed project file.
- **Otherwise read back.** The file is read out of the sandbox into engine scratch (at most
  `Compute:Render:SourceMaxBytes` each), keyed by its SHA-256 (`sandbox-sha256:<hash>` in place of the
  source key), probed, copied and verified with the same recipe and cache, and the copy is staged back
  under the same name. A second restore of the same clip reads and hashes it, and makes nothing.
- **Recorded either way**, so the cache sizing and `failingVideos` see its real size.
- **Bounded and fail-open.** At most 12 videos and 2 GiB read out of the sandbox per pass, and 10
  minutes (checked between videos; each encode keeps its own timeout). Any failure leaves that video
  exactly as restored and logs a Warning; the restore, the revision and the render go on. ffmpeg still
  never runs in the sandbox.

### Staged files, video read failures and the final render

Run 36df4287 failed three Author attempts the same way: `StageProjectFile` was called with made-up
names (`ad-01.mp4`), the composition kept `staticFile('ad-01.mp4')`, every `remotion still` died with
`Could not extract frame from compositor Error: Request closed` (offthread-video-server), and the
agent never called the full render. Four guards now cover that path:

- **`StageProjectFile` misses name real files.** A reference that matches nothing (still matched
  exactly: id, storage key, original name or path, or `directoryPath/fileName`) stages nothing and
  answers `closestFiles`: the 5 nearest project files by case-insensitive name and path similarity,
  each with `id`, `fileName`, `directoryPath`, `mimeType`, `sizeBytes`. The miss is logged at
  Information with the reference.
- **Missing `staticFile` targets are refused before Remotion runs.** `RenderPreviewStills`,
  `CheckLayout` and `RenderVideoAndUploadToStorage` read `src/**` (same bounds as the network-font
  check), collect every `staticFile('...')`/`staticFile("...")` literal outside comments, and answer
  `{ok:false, error:"MISSING_STATIC_FILE: ...", missing:[{path, usedIn}]}` when a target is not under
  `public/`. Template literals and computed paths are not judged. If the scan cannot run, the
  command runs as before.
- **A video frame read failure says what it is.** When a still, a layout check or a render fails
  with `Could not extract frame`, `offthread-video-server`, or a `delayRender()` timeout on Remotion's
  `/proxy?src=` frame route in its output, the failure gains `videoHint` (a video the composition
  plays is missing, in an unsupported codec, or too large) and `stagedVideos` (the video files under
  `public/` with their sizes and, when recorded at staging, `width`, `height`, `fps`, `proxied`).
  When the output names the video being read, `failingVideos` lists it with the same facts and the
  hint starts "Remotion could not read a frame of public/x.mp4 (2160x3840)."
- **One reminder to render.** An Author that answers without a successful full render (and whose
  renders did not hit the failed-render cap) gets one more turn in the same attempt, told to call
  `RenderVideoAndUploadToStorage` now and to stage any missing files first. If it still does not
  render, the step fails with "the agent never rendered the final video".

### Layout lint

`sandbox/template/reelbolt/check-layout.mjs` ships in the image at
`/opt/remotion-template/reelbolt/check-layout.mjs`. The engine's `CheckLayout` tool runs it as
`node /opt/remotion-template/reelbolt/check-layout.mjs --composition=<id> --frames=<n,...>
--min-text-px=<px> --output-scale=<s>` in `/workspace`. It bundles the workspace with the
workspace's own `@remotion/cli` (so `remotion.config.ts` applies as it does for `remotion still`)
into `out/layout-lint-bundle`, which it deletes afterwards. It opens Remotion's headless shell, renders a throwaway
still of each frame (at most 12) and, while the frame is on the page, measures every element that holds
text directly:

| kind | what it means |
|---|---|
| `overflow` | the text does not fit its own box: `scrollWidth`/`scrollHeight` exceed the client size by more than 1px with overflow not `visible`, or the text spills out of a box that paints a background or border |
| `clipped` | an ancestor whose overflow is not `visible` cuts part of the text off (the composition's own frame is left to `outside_safe_area`) |
| `outside_safe_area` | visible text reaches into the outer 5% of the frame |
| `logo_low_contrast` | a loaded, same-origin `<img>` or SVG `<image>` that is mark-sized (at least 8px on screen; neither natural side above 1024px; covering at most 25% of the frame; not wider than 8:1) whose alpha-weighted mean ink colour has WCAG contrast below 3.0 against what is painted behind it. The background is read at five points of the logo's box from the painted stack below it (siblings and ancestors alike: opaque background colours composited, a gradient taken as the mean of its stops); a point behind a video, canvas, image or unparseable background is unknown. The issue is raised only when at least three points are known and every known point fails. The issue carries only the file name, `logoContrast`, `logoLuminance` and `backgroundLuminance`. |
| `text_low_contrast` | visible text whose colour (computed `color` composited with its effective opacity) has WCAG contrast below 4.5:1 (below 3:1 for large text: computed `font-size` at least 24px, or at least 18.66px with weight 700 or more) against the colour painted behind it. The background is read at five points inside the text box, the same way as for `logo_low_contrast` but including the element's own background; a point behind a video, canvas, image or `url()` background is unknown, and the issue needs at least three known points with every known point failing. Text with a `text-shadow`, a text stroke, `background-clip: text` or a transparent colour is skipped. At most 10 per frame. The issue carries `textContrast`, `requiredContrast`, `textColor` and `backgroundColor` (rgb strings). |
| `too_small` | computed `font-size` × the element's own transform scale × `--output-scale` is below `--min-text-px` |

A logo is skipped, never reported, when the background behind it is unknown (a `url()` image, or a video, image or canvas ancestor, or no opaque colour) or when the image cannot be read. Known limit: an SVG `<image>` whose file is not loaded synchronously when the frame is measured is skipped.

Text that is entirely off the frame, `visibility: hidden` or near-transparent (effective opacity below
0.05) at that frame is skipped. The same element and kind at several frames is one issue (`alsoAtFrames`);
at most 40 issues are printed, ordered by kind. The script prints one line, `@@REELBOLT_LAYOUT@@` followed
by JSON: `{ok, compositionId, width, height, frames, textElements, issuesTotal, counts, truncated,
issues: [{frame, kind, text, path, box, fontPx, onScreenPx, detail}]}`. A failure prints the same
marker with `{ok: false, stage, error}`. The composition's code runs in the page exactly as it does for a render,
inside the sandbox. The script reaches nothing over the network.

> **Stable headless-shell path:** Remotion downloads `chrome-headless-shell` into an
> architecture-specific directory (different directory and executable names on amd64 and
> arm64). The `sandbox-runtime` image build resolves the real executable and links it to
> `/workspace/node_modules/.remotion/headless-shell` (a relative symlink, so it survives the
> template copy into `/workspace`); the build fails if no executable is found. Nothing names an
> architecture: `sanitizeExecRequest`, the template's `render` script and the engine's tools
> all use that one path. There is **no** fallback to a system Chromium (the image has none). An
> exec first checks the path inside the container and returns a clear error if it is missing.

### Structure

```
template/
├── package.json
├── tsconfig.json
└── src/
    ├── index.ts        # Remotion entry point — calls registerRoot(Root)
    └── root.tsx        # Default composition: 6-second 1920×1080 intro animation
```

### `index.ts`

Registers the root component with Remotion:

```ts
import { registerRoot } from 'remotion';
import { Root } from './root';
registerRoot(Root);
```

### `root.tsx`

Defines the default `Root` component and a single `Main` composition (1920×1080, 30 fps, 180 frames = 6 s). The composition renders a violet radial-gradient background with the "ReelBolt" wordmark fading in and gently translating vertically. AI agents replace/extend this file with project-specific content.

### `package.json` — Preinstalled Dependencies

| Package | Purpose |
|---|---|
| `remotion` + `@remotion/cli` + `@remotion/renderer` | Core Remotion framework and CLI |
| `@remotion/captions`, `fonts`, `gif`, `layout-utils`, `lottie`, `media-utils`, `motion-blur`, `noise`, `paths`, `shapes`, `three`, `transitions`, `zod-types` | Remotion companion packages, all pinned to the same version as `remotion` (4.0.545) |
| `react` + `react-dom` | React 19.3 (peer dep of Remotion) |
| `@react-spring/web`, `framer-motion`, `gsap` | Animation libraries (gsap: timeline animation driven by the frame, see the ReelBolt gsap guide in the remotion-markup skill preface) |
| `@react-three/fiber` + `@react-three/drei` + `three` | React bindings for Three.js and 3D graphics |
| `d3` (+ `@types/d3` dev) | Data-driven visualisations |
| `lottie-web`, `simplex-noise`, `zod` | Lottie playback, procedural noise, schema validation (`zod` pinned to 4.6.7, the exact version the `@remotion/*` 4.0.545 packages depend on, so Remotion prints no zod version-mismatch warning) |
| `lucide-react`, `@phosphor-icons/react` | Icon sets |

`@remotion/google-fonts` is no longer installed: it fetches `fonts.gstatic.com` at load time and the sandbox has no network (the engine still refuses a project that imports it, `NETWORK_FONT`, below). The authoritative list is `sandbox/template/package-catalog.json`, which a test keeps equal to `package.json` dependencies. All versions are exact and `package-lock.json` is committed (integrity hashes). The image installs with `npm ci --ignore-scripts`, and the build fails if any locked dependency declares an install script other than the reviewed `esbuild` one (its platform binary ships in the optional `@esbuild/*` package, so the script is not needed). The engine's `InstallNpmPackages` tool answers "already installed" for catalog names and refuses all others (no network); it does not call the sandbox `POST /packages` endpoint.

### `package.json` — Available Scripts

| Script | Command | Description |
|---|---|---|
| `build` | `remotion bundle src/index.ts --out-dir build` | Bundle to static assets |
| `render` | `remotion render src/index.ts Main out/video.mp4 --chromium-executable=/workspace/node_modules/.remotion/headless-shell` | Full video render via bundled headless shell (preferred over system Chrome) |
| `compositions` | `remotion compositions src/index.ts` | List registered compositions |
| `typecheck` | `tsc --noEmit` | TypeScript type check only |

---

## Customizing the sandbox

- **Fonts.** The image bundles the families in `sandbox/fonts/fonts.json` (`SandboxFonts.FamilyList`). For any other family the agent calls `StageFont`: a Google Fonts family (only when `Sandbox:GoogleFonts` is enabled; the engine fetches it, the sandbox still has no network) or a project font file. It writes the file under `public/fonts/<Family>/` and returns a JSON result whose `snippet` is TypeScript to paste at module top level, outside any component: `import {staticFile} from 'remotion'; import {loadFont} from '@remotion/fonts';` plus one `loadFont({family, url: staticFile('fonts/<Family>/<file>.ttf'), weight, style})` call per staged file. No `@font-face` is returned; the component uses the plain family name in `fontFamily`, and `loadFont` calls `delayRender` itself. A staged project font is loaded with the same snippet. Remote font loads (`@remotion/google-fonts`, a remote `url()`, `@import`, `FontFace` or `loadFont` from a remote URL) stay refused (`NETWORK_FONT`).
- **Packages.** The template list is `sandbox/template/package-catalog.json`, pinned and baked into the image. `InstallNpmPackages` only reports which catalog packages are installed. Arbitrary installs stay refused: no network, and an unreviewed package is untrusted code in the render.
- **Blocks.** `inference/blocks/` holds tested, editable Remotion blocks. `ListBlocks`, `GetBlock` and `AddBlock` browse them and copy one into `src/blocks/` with usage notes (skill `reelbolt-blocks`).
- **`.json` staging.** `StageProjectFile` accepts `.json` (Lottie, chart data, glTF-JSON) only while tool screening is Off (the shipped state), after a parse check and an 8 MB cap; with screening on it is refused again because JSON can smuggle code past the guardrail. Code and markup files (`.js .jsx .ts .tsx .mjs .cjs .mts .cts .wasm .html .htm`) are always refused, and `.json` is refused whenever tool screening is on.
- **`npm run` arguments.** The `/exec` allowlist takes script arguments only after a single `--`, each of the form `--flag` or `--flag=value` (for example `npm run render -- --props=...`).
- **Still refused:** network access, installs outside the catalog, remote fonts or media. Agents are told not to edit `src/index.ts` (prompt guidance only; no code enforces it).

## Docker Build

The `sandbox/Dockerfile` performs a three-stage build with two publishable
targets: `sandbox-runtime` (untrusted, what sandboxes run) and `control-plane`
(trusted, what the service runs). `docker-compose.yml` builds both — the
`sandbox-runtime` service builds the runtime image and exits, and
`sandbox-executor` waits on it via `service_completed_successfully`, because the
image must exist on the daemon before any `docker run` of it can succeed.

### Stage 1 — `gobuild` (`golang:1.25-alpine`)

1. Downloads Go module dependencies with retry logic (up to 5 attempts).
2. Compiles the Go server as a static binary: `CGO_ENABLED=0 go build -ldflags="-s -w -X main.Version=<VERSION>"`.
3. The `VERSION` build arg (defaults to `docker`) is injected into the `main.Version` variable, which is logged on startup.

### Stage 2 — `sandbox-runtime` (`node:22-bookworm-slim`) — the untrusted image

1. Installs system packages via `apt-get` (this is a Debian base, not Alpine): `ffmpeg`, the
   Chrome/Chromium *dependency* libraries Remotion's headless renderer needs at runtime
   (`libnss3`, `libatk-bridge2.0-0`, `libgbm1`, `libgtk-3-0`, etc. — there is no `chromium`
   browser package installed directly), and the fonts. Fonts are bundled, not downloaded: the
   image ships the OFL set in `sandbox/fonts/` (copied to `/usr/share/fonts/truetype/reelbolt/`
   and indexed with `fc-cache -f`): Inter, Montserrat, Poppins, DM Sans, Space Grotesk, Chakra
   Petch, Bebas Neue, Anton, Playfair Display, DM Serif Display and JetBrains Mono, next to
   `fonts-liberation` (Liberation Sans/Serif/Mono) and `fonts-noto-color-emoji`. No network
   fonts: a template that loads fonts from a URL at render time (`@remotion/google-fonts`,
   `@import`, `url(...)` to a font host) cannot resolve in the sandbox, which runs with no
   network. The engine guards against it: before every composition listing, still and render
   (`GetCompositionInfo`, `RenderPreviewStills`, `RenderVideoAndUploadToStorage`,
   `RunSandboxRemotionCommand`, the `render`/`compositions` npm scripts and the graphic runner's
   still) `NetworkFontPreflight` reads the project's `src/**` sources (ts, tsx, js, jsx, mjs, css;
   `node_modules` skipped; 256 KiB per file, 4 MiB, 400 files and 200 directories in total) and
   refuses a network font load at once with `NETWORK_FONT: <file>:<line> loads a font from the
   network ... The sandbox has no internet. Use an installed font by CSS font-family name: ...`.
   Comments and mentions that load nothing are not refused, and a source it cannot read is
   skipped (the render then goes ahead as before). Upstream commit and licence details are in `sandbox/fonts/UPSTREAM.md`. Note there is no separate `PUPPETEER_EXECUTABLE_PATH` set in the
   image — see the troubleshooting note below for how the actual headless binary is resolved.
2. Copies `template/package.json` and runs `npm ci --ignore-scripts` (against the committed lockfile) to pre-install all Remotion dependencies
   into the image at `/opt/remotion-template/node_modules` — this is what actually provisions the
   `chrome-headless-shell` binary Remotion renders with (see below), not a system package.
3. Copies the rest of the template source files and drops to the `node` user.

   Deliberately absent: the Docker CLI, `curl`, `wget`, `gnupg`. See
   [Two images, not one](#two-images-not-one).

    > **Troubleshooting:** When workspaces run `npx remotion render` directly the CLI
    > defaults to a bundled `chrome-headless-shell`, pre-downloaded at image build time and
    > linked to the stable path `/workspace/node_modules/.remotion/headless-shell` (see "Stable
    > headless-shell path" above). The Go executor and the engine's tools append
    > `--chromium-executable=/workspace/node_modules/.remotion/headless-shell` to render
    > invocations. If you execute remotion manually, add the flag yourself.
    > The image is built for `linux/amd64` and `linux/arm64`; CI builds both and smoke-renders on arm64.
### Stage 3 — `control-plane` (`FROM sandbox-runtime`) — the trusted service image

1. Returns to `root` and installs `curl` + `gnupg`, then Docker's official
   `docker-ce-cli` from `download.docker.com`'s apt repo — the `docker` binary this
   service shells out to in order to launch each per-execution sandbox container.
2. Copies the Go binary from Stage 1.
3. Exposes port `8080` and sets the entrypoint to the Go binary.

Building on the runtime stage is purely for layer reuse. Nothing added here may
ever move up into Stage 2.

> **Note:** The two targets are **not** interchangeable. `sandbox-runtime` is what
> untrusted code executes in and must stay minimal; `control-plane` adds the Docker
> CLI and the Go binary and holds the Docker socket. They were previously a single
> image, which put a Docker client inside every untrusted container. `SANDBOX_IMAGE`
> must always point at the runtime target.

---

## Published runtime image

The `sandbox-runtime` target is published to GitHub Container Registry as
`ghcr.io/reelbolt/reelbolt-sandbox-runtime` (public, so desktop runners and cloud workers pull
it anonymously). Consumers pin it by **digest**, never by tag.

- **Release trigger.** Pushing a tag `runtime-vMAJOR.MINOR.PATCH` runs
  `.github/workflows/runtime-image-release.yml`. It builds `linux/amd64` and `linux/arm64`,
  pushes `:<version>`, and attaches an SBOM and build provenance as OCI attestations.
- **Signing.** The index and each per-architecture manifest are signed with keyless cosign
  (GitHub OIDC; no key to store or rotate). Verify with:
  `cosign verify ghcr.io/reelbolt/reelbolt-sandbox-runtime@sha256:<digest> --certificate-identity-regexp '^https://github.com/ReelBolt/reelbolt/\.github/workflows/runtime-image-release\.yml@refs/tags/runtime-v' --certificate-oidc-issuer https://token.actions.githubusercontent.com`
- **Lockfile.** A bot PR rewrites `sandbox/runtime-image.lock` (`{"version", "digest"}`). The
  runner build embeds this file, so a runner release always names an exact, verifiable image.
  Until the first release the file holds an empty digest.
- **Retention policy.** Keep every digest referenced by any runner release from the last 12
  months, plus the current one. Do not delete such versions from GHCR; older unreferenced
  versions may be pruned. Deleting a referenced digest breaks that runner release's first pull.
- **Size.** The image is about 1.5 GB, so a first pull is slow; the runner shows progress.
