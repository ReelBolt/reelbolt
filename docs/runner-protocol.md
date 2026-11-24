# Runner protocol

The runner protocol is how the platform hands compute work (Remotion renders, video compilation, video analysis extraction) to a machine outside the engine process: ReelBolt's own cloud worker pool or a customer's desktop runner. This page is the normative specification. It is implemented by the Go `runner-gateway`, the Go worker library (`reelbolt-worker` and the desktop app), and the .NET engine. The reasoning behind it is in the design record `docs/design/runner-protocol-adr.md` in the repository. Golden example messages for every message type live in `sandbox/protocol/fixtures/` and are used by contract tests on both the Go and .NET sides.

Words in this document follow RFC 2119: MUST, MUST NOT, SHOULD.

## Overview and scope

A **runner** is a process that dials out to a **gateway** over a WebSocket, advertises what it can do, and executes **jobs** the gateway assigns to it. There are two kinds:

- **CloudPool**: our own worker VMs, authenticated by a pool credential, serving any organisation's jobs with `target=CloudPool`.
- **Desktop** (OrgRunner): a customer's machine, authenticated by a per-device Ed25519 key, serving only jobs of the organisation it was paired to.

Three job types exist in v1: `remotion.workspace-session`, `video.compile` and `video.analyze.extract`. All three are **sessions**: the engine opens a job, then sends a stream of **ops** (write a file, run ffmpeg, publish an object) and reads each result before deciding the next op. Binary data never travels over the socket; it moves through presigned object-store URLs.

Everything else (model calls, provider selection, ASR, vision captioning, billing) stays in the engine and Inference API. The protocol states three invariants that hold in every section:

1. **No message or op ever carries a provider credential.**
2. **Runner output is untrusted** and is verified by the platform.
3. **Billing is priced from the platform-side job spec**, never from runner-reported data.

Self-hosted installs keep `Compute:Mode=Local` (the existing `sandbox-executor` plus in-process ffmpeg) and need none of this.
This page is the **wire** specification. The operator's and developer's view — the three targets, the
selector's decision table, how to turn the fabric on, what is not wired yet, and the deployment
hazards — is [compute-fabric.md](compute-fabric.md).

## Transport and envelope

The runner dials `wss://runner.<domain>/v1/connect`. The connection is outbound only; a runner MUST NOT listen on any port. TLS is mandatory in every build that is not a development build. The WebSocket subprotocol is `reelbolt.runner.v1`.

Every message is one WebSocket text frame containing a UTF-8 JSON object, the **envelope**:

| Field | Type | Required | Meaning |
|---|---|---|---|
| `v` | integer | yes | Envelope major version. `1` in this spec. |
| `type` | string | yes | One of the message types in the catalogue. |
| `id` | string | yes | Sender-unique message id (UUID or ULID). Used for logs and for matching an `accept`/`reject` to its `assign`. |
| `jobId` | string (UUID) | job-scoped messages | The job the message belongs to. |
| `sessionId` | string (UUID) | session-scoped messages | The session (equal to one open job in v1). |
| `payload` | object | yes | Type-specific body; `{}` when empty. |

Limits:

- The serialized `payload` MUST NOT exceed **4 MiB** (4,194,304 bytes). A receiver MUST set its read limit to the payload limit plus 64 KiB and close with code 1009 on excess.
- Binary data MUST NOT be sent over the socket. Content that does not fit inline moves through a presigned URL (see "Presigned URLs and object storage").
- Unknown fields MUST be ignored. A receiver that gets an unknown `type` MUST ignore the message, except an unknown op, which gets an `op.response` with error code `unsupported_op`.
- Field names are camelCase. Timestamps are RFC 3339 UTC strings. Durations are integer seconds unless the name ends in `Ms`. Byte counts are integers.

WebSocket close codes used by this protocol: `1000` normal, `1001` gateway going away, `1009` message too large, `4400` unsupported protocol version or malformed hello, `4401` unauthenticated or revoked device, `4403` forbidden (organisation disabled), `4408` heartbeat timeout, `4409` superseded by a newer connection from the same device, `4429` rate limited.

## Message catalogue

Twenty-one message types (the catalogue grew from fifteen with the six `llm.*` relay messages). "R to G" means runner to gateway; "G to R" the reverse.

| Type | Direction | Scope | Purpose |
|---|---|---|---|
| `hello` | R to G | connection | First message. Identity, credential proof, capabilities, any jobs the runner believes it still holds. |
| `welcome` | G to R | connection | Accepts the connection, returns negotiated limits and the runner's resolved identity. |
| `heartbeat` | both | connection | Liveness and lease renewal, every 15 s. |
| `ready` | R to G | connection | Declares free slots; the gateway assigns work only against free slots. |
| `assign` | G to R | job | Offers a job and carries the job token. |
| `accept` | R to G | job | Runner takes the job. |
| `reject` | R to G | job | Runner declines; the job returns to `Queued` without consuming an attempt. |
| `progress` | R to G | job | Non-authoritative progress (stage, percent). |
| `op.request` | G to R | session | One operation for the session. |
| `op.response` | R to G | session | Result or error for one op. |
| `complete` | R to G | job | Job finished (succeeded or acknowledged cancellation). |
| `fail` | R to G | job | Job failed, with a class and a retry hint. |
| `cancel` | G to R | job | Stop the job now. |
| `drain` | both | connection | Stop taking new work; finish or abandon current jobs. |
| `bye` | both | connection | Orderly goodbye immediately before closing. |
| `llm.request` | G to R | connection | One request for the user's local model server (not job scoped). |
| `llm.response.start` | R to G | connection | Status and content type of the local server's answer. |
| `llm.response.chunk` | R to G | connection | One piece of the answer body, in order. |
| `llm.response.end` | R to G | connection | The answer is complete. |
| `llm.error` | R to G | connection | The request could not be served. |
| `llm.cancel` | G to R | connection | Stop serving a request. |

Job-scoped runner to gateway messages (`accept`, `reject`, `progress`, `op.response`, `complete`, `fail`) MUST carry `jobId`, `payload.leaseEpoch` and `payload.jobToken`; the gateway MUST reject any with a stale epoch or invalid token (see "Job lifecycle, leases and fencing"). Session-scoped messages additionally carry `sessionId`.

Payload shapes (all fields required unless marked optional):

- `hello`: `protocolVersion` (string, `"1.0"`), `auth` (see "Connection handshake and authentication"), `capabilities` (see "Capabilities"), `running` (optional array of `{jobId, leaseEpoch}` for jobs the runner still holds after a reconnect).
- `welcome`: `runnerId`, `kind` (`Desktop` or `CloudPool`), `orgId` (null for the pool), `protocolVersion`, `heartbeatSec` (15), `deadAfterSec` (45), `maxInlineBytes` (4194304), `gatewayInstance`, `serverTime`, `resumed` (array of `{jobId, leaseEpoch}` the gateway kept; any `running` entry not listed here will be followed by a `cancel`).
- `heartbeat` from runner: `freeSlots`, `running` (array of `{jobId, leaseEpoch}`). From gateway: `serverTime`, `leases` (array of `{jobId, ok}`; `ok:false` means the lease is lost and the runner MUST stop that job).
- `ready`: `freeSlots`.
- `assign`: `jobType`, `leaseEpoch`, `jobToken`, `attempt`, `timeoutSec`, `limits` (`maxOutputBytes`, `idleTimeoutSec`), `requires` (digest and version pins), `spec` (job-type-specific, see "Job types"), `orgId`. Optionally `display` (`workspaceName`, `projectName`, `workflowName`, `projectId`, `workflowId`, `executionId`, each optional): the names a runner's own UI shows next to the job. The gateway reads them once, best effort, within 2 seconds, from the job's own organization only, and leaves the block out when the read fails or is slow; a runner MUST NOT authorize, route or decide anything by it, and an older runner ignores it.
- `accept`: `leaseEpoch`, `jobToken`.
- `reject`: `leaseEpoch`, `jobToken`, `reason` (`busy`, `capability_mismatch`, `shutting_down`, `paused_by_user`, `no_container_runtime`), `message` (optional).
- `progress`: `leaseEpoch`, `jobToken`, `opId` (optional), `stage` (string), `percent` (0..100, optional), `detail` (optional string).
- `op.request`: `opId`, `op`, `args`, `timeoutSec`.
- `op.response`: `leaseEpoch`, `jobToken`, `opId`, `ok`, and either `result` (object) or `error` (`{code, message, retryable}`).
- `complete`: `leaseEpoch`, `jobToken`, `outcome` (`succeeded` or `cancelled`), `result` (optional object, informational only), `metrics` (optional, informational only).
- `fail`: `leaseEpoch`, `jobToken`, `class` (`platform`, `runner` or `input`), `code`, `message`, `retryable`.
- `cancel`: `reason` (`user`, `execution_ended`, `lease_lost`, `revoked`, `shutdown`), `graceSec` (10).
- `drain`: `reason`, `deadlineSec` (optional).
- `bye`: `reason`.
- `llm.request`: `requestId`, `method` (`POST` or `GET`), `path` (`/v1/chat/completions` or `/v1/embeddings` with `POST`, or `/v1/models` with `GET`; anything else is refused), `body` (JSON text, at most 3 MiB), `stream`, `timeoutSec` (0 to 900).
- `llm.response.start`: `requestId`, `status`, `contentType` (optional).
- `llm.response.chunk`: `requestId`, `seq`, `data` (valid UTF-8, at most 256 KiB).
- `llm.response.end`: `requestId`, `bytes`.
- `llm.error`: `requestId`, `code` (`disabled`, `unsupported_path`, `busy`, `server_unreachable`, `timeout`, `too_large`, `cancelled`, `bad_request`), `message` (optional).
- `llm.cancel`: `requestId`.

The `llm.*` messages carry no `jobId`, `leaseEpoch` or `jobToken`, no URL, no host and no header: the runner picks the target from its own settings. See [`local-llm-gateway.md`](local-llm-gateway.md).

The fixtures in `sandbox/protocol/fixtures/` show each of these concretely. A fixture file is named `<type>.<variant>.json`.

## Connection handshake and authentication

The runner speaks first. The sequence is: WebSocket upgrade, runner sends `hello`, gateway verifies it and answers `welcome` (success) or closes with `4400`, `4401` or `4403`. No other message is accepted before `welcome`; the gateway MUST close a connection that sends anything else with `4400`, and MUST close a connection that sends no `hello` within 10 seconds.

`hello.auth` has one of two shapes.

**Desktop device** (`kind: "device"`): `{kind, deviceId, ts, nonce, sig}`.

- `ts` is the runner's clock as RFC 3339 UTC; the gateway MUST reject `|now - ts|` above 120 seconds.
- `nonce` is at least 16 random bytes, base64url. The gateway MUST keep a replay cache of `(deviceId, nonce)` for at least 240 seconds and reject repeats.
- `sig` is the base64url Ed25519 signature over the UTF-8 string `reelbolt-runner-hello-v1|<deviceId>|<ts>|<nonce>|<gatewayHost>`, where `gatewayHost` is the hostname the runner dialled. Binding the host stops a hostile gateway replaying the hello at another gateway.
- The gateway verifies `sig` against `runner_devices.public_key` for `deviceId` and MUST refuse the connection with `4401` when the device is unknown or `revoked_at` is set. The key pair is created on the device at pairing and the public key is registered then; the private key never leaves the device.

**Cloud pool worker** (`kind: "pool"`): `{kind, credential, workerName}`. The credential is the pool secret from the secrets manager. A pool runner has `OrganizationId = null` and may be assigned any job with `target=CloudPool`.

Rules that apply to both:

- If the same `deviceId` (or pool `workerName`) connects while a previous connection is open, the new connection wins and the old one is closed with `4409`.
- The gateway re-checks `revoked_at` at least every 60 seconds for open desktop connections as a backstop and also consumes the `RunnerDeviceRevoked` event; on revocation it sends `bye` and closes with `4401`, and handles the device's jobs as in "Job lifecycle, leases and fencing".
- Credentials, device signatures and job tokens MUST NOT be logged. Presigned URLs MUST be logged with the query string removed.
- Nothing in `hello` or `welcome` is a provider credential. Provider keys have no representation in this protocol.

## Capabilities

`hello.capabilities` tells the gateway what the runner can do. The gateway matches jobs to runners on it and stores it as `runner_devices.capabilities_json`.

| Field | Type | Meaning |
|---|---|---|
| `protocolVersion` | string | Highest protocol version the runner speaks, `"1.0"`. |
| `agentVersion` | string | Worker library or desktop app version. |
| `os`, `arch` | string | `linux`/`darwin`/`windows` and `amd64`/`arm64`. |
| `slots` | integer | Maximum concurrent jobs. |
| `container` | string | `none`, `docker` or `podman`. |
| `containerMode` | string, optional | `rootful`, `rootless` or `vm`, so policy can later refuse some modes. |
| `runtimeImageDigest` | string or null | Digest of the Remotion runtime image the runner has pulled; null when `container` is `none`. |
| `ffmpegVersion` | string | Version of the ffmpeg the runner will execute. |
| `fontsetVersion` | string | Version tag of the bundled font set used by drawtext overlays. |
| `jobTypes` | string array | Subset of `remotion.workspace-session`, `video.compile`, `video.analyze.extract`. |
| `maxOutputBytes` | integer | Largest single object the runner is willing to publish. |
| `localLlm` | object, optional | `{enabled, serverKind, models, maxConcurrent}`: the runner shares a model server of its owner's. At most 100 model names of at most 200 characters. Never carries a URL or credential. Absent or `enabled:false` means the runner serves no `llm.*` requests. See [`local-llm-gateway.md`](local-llm-gateway.md). |

Rules:

- A runner with `container: "none"` MUST advertise only `video.compile` and `video.analyze.extract`. `remotion.workspace-session` requires a verified container runtime (Docker Engine 24+ or Podman 4.6+, with a live-probed internal no-egress network).
- The gateway MUST NOT assign a job whose `requires` are not met: `jobType` in `jobTypes`, `runtimeImageDigest` equal to the digest the engine requires for Remotion, `fontsetVersion` at least the minimum for compile, and `maxOutputBytes` at least the job's expected output cap.
- Capabilities are self-reported and therefore untrusted. A runner that lies about them can only fail its own jobs or receive work it then rejects; it gains nothing, and its outputs are verified regardless.

## Job types

All v1 job types are sessions. The gateway creates one `runner_sessions` row per job; `sessionId` is the job's session. A session is pinned to a single runner for its whole life and is the unit that the engine opens once per workflow execution (Remotion) or once per step (media).

**`remotion.workspace-session`.** Interactive and long-lived, one per workflow execution. The runner keeps a workspace (a copy of the Remotion template) inside a no-egress container. `assign.spec` holds `{executionId, template: {name, version}}`. The op set is in "Remotion workspace ops".

**`video.compile`.** One VideoCompile step. A media session whose ops are only ffmpeg-level (see "Media session ops and argv validation"). `assign.spec` holds `{executionId, stepId, mode}` where `mode` is `Reencode` or `StreamCopy`.

**`video.analyze.extract`.** The extraction phase of one VideoAnalyze step: probe, silence and shot detection, WAV extraction, frame grid, keyframes, chroma tracking inputs. Same media op set. ASR transcription, vision captioning, the derush pre-pass and input screening are not job types; they run in the engine because they need provider credentials. Object tracking (`TrackObjects`) is platform-only in v1.

**Why sessions rather than one-shot plans.** The executors decide each next step from the previous result. `VideoCompileStepExecutor` (about 9,500 lines) probes files after producing them, at roughly ten call sites, and builds the next argv from the probe; `VideoAnalyzeStepExecutor` (about 4,500 lines) picks which shots to caption and which keyframes to extract from C# analysis of an earlier ffmpeg pass; the Remotion agent loop writes, builds, reads errors and edits. Precomputing a plan would mean rewriting those executors as a plan compiler and interpreter. A session keeps control flow in the engine and moves only the execution of each primitive to the runner. A one-shot job type (a single request, a single result, requeue on loss) is reserved by the lifecycle rules but none is defined in v1.

**Session limits.** `assign.timeoutSec` is the maximum session lifetime (default: the execution's remaining budget, at most 14,400). `limits.idleTimeoutSec` (default 1,800) closes a session with no op for that long. The engine closes a session explicitly with the `close` op; the gateway closes it on engine execution end, on timeout and on idle.

**One op in flight.** A session handles one op at a time. The gateway MUST answer a second concurrent op request with HTTP 409 `op_in_flight` on its internal API and not forward it. `cancel` is the only message allowed while an op runs. The engine never sends a second op on a session itself (it serialises its own ops), gives every op an explicit deadline, and, when its own request for an op times out or the gateway answers `op_in_flight`, asks again under the SAME `opId` until the op answers or its deadline plus a 60 s grace passes (`Compute:MediaSession:OpTimeoutSeconds`, `OpSettleGraceSeconds`, `OpPollIntervalMilliseconds`).

**One open session per execution, and the ENGINE serialises it.** The gateway opens the `runner_sessions` row at assignment, and that table carries a partial `UNIQUE(execution_id) WHERE closed_at IS NULL`: at most one session may be open for a workflow execution at a time. The gateway tolerates a violation (a savepoint, logged, and the job still runs — just without a session row), because the constraint is the schema's; the invariant is the engine's to keep, and it matters because a job with no session row has no `runner_sessions.gateway_instance` for op routing to use. So the engine MUST NOT have two runner sessions outstanding in one execution: it holds a per-execution lease from the moment it creates a job (a runner may be assigned it at any instant) until that job reaches a terminal state, and a second session of the same execution queues behind the first. Different executions are independent and never queue behind each other. The engine's implementation is `WorkflowEngine/Services/Compute/ExecutionSessionGate.cs` (WP D6).

## Engine to gateway

The engine does not speak the WebSocket protocol above; the gateway does. The engine calls the gateway's cluster-internal API instead, which is where jobs are created and cancelled and where "is a runner online?" is asked:

- `POST /internal/v1/jobs` — create a job, idempotent on `sha256(executionId|stepId|iteration|purpose)`. 201 when it created the row, 200 when the key already existed (the SAME job comes back).
- `GET /internal/v1/jobs/{id}?wait=30s` — read one job, long-polling until it is terminal. A read that runs out of window is a normal answer, not an error.
- `DELETE /internal/v1/jobs/{id}` — cancel.
- `GET /internal/v1/runners?org=&caps=` — which runners are online with the capabilities a job needs. It is answered per gateway instance, so a "no" means "not on this replica" and the engine treats it as a hint it may route on, never as a promise a job will be claimed.

Every route requires the shared `RUNNER_INTERNAL_TOKEN`, and a gateway with none configured answers `503 internal_api_disabled` for all of them. That is the answer a deployment which never installed the fabric gives: the engine MUST read it as "the compute fabric is not deployed here" and degrade (with `Compute:Mode=Local` it never calls the API at all), never as an error that fails a workflow. The engine client is `WorkflowEngine/Services/Compute/RunnerGatewayClient.cs` (WP D6).

## Remotion workspace ops

These ops are valid only in a `remotion.workspace-session`. Each travels as `op.request` with `args` and returns `op.response`. Paths are relative to the workspace root, are normalised, and MUST NOT be absolute or escape the root; the worker enforces this with an `os.Root`-style containment check (the same containment as `sandbox/pkg/container`) and rejects any path that resolves through a symlink out of the workspace. Any job or op field that looks like a host path MUST be rejected with `invalid_path`.

| Op | `args` | `result` |
|---|---|---|
| `writeFile` | `path`, `content`, `encoding` (`utf8` or `base64`) | `{bytes, sha256}` |
| `readFile` | `path`, `maxBytes` (at most 2.75 MiB for a binary read; see the media `read` row) | `{content, encoding, bytes, truncated:false}`. A file larger than `maxBytes` is an error `file_too_large`, never a silent short read. |
| `listFiles` | `path` (optional), `depth` (optional) | `{entries:[{path, kind, bytes}]}` |
| `deletePath` | `path` | `{deleted}` |
| `exec` | `command`, `args`, `timeoutSec` | `{exitCode, stdout, stderr, durationMs}`, with stdout and stderr each capped inline and flagged `truncated` |
| `status` | none | `{workspaceReady, diskUsedBytes, openProcesses}` |
| `stage` | `url`, `path`, `maxBytes`, `sha256` (optional) | `{bytes, sha256}`. The runner GETs the presigned URL into the workspace. |
| `publish` | `path`, `putUrl`, `maxBytes`, `contentType` | `{bytes, sha256}`. The runner PUTs the file to the presigned URL. |
| `render` | `compositionId`, `args` (render flags), `putUrl`, `outputName` | `{bytes, sha256, durationMs}`. Runs the render in the container and publishes the result. |
| `hydrate` | `manifest` (array of `{path, sha256, url}`) | `{restored, skipped}`. Rebuilds a workspace on a fresh runner from the engine's journal. |
| `close` | none | `{}`. Destroys the workspace and ends the session. |

`exec` allowlist. The worker MUST re-validate every exec and reject anything else with `exec_not_allowed`. It is exactly the allowlist of `ValidateExec` in `sandbox/pkg/container`:

- `npm run <script>` where script is one of `build`, `render`, `typecheck`, `compositions`, `lint`;
- `npx remotion <sub>` where sub is one of `render`, `still`, `compositions`;
- `node /opt/remotion-template/reelbolt/check-layout.mjs [--flag=value ...]`: exactly that one script, which the
  runtime image ships (never a workspace file), followed by at most 8 arguments, each matching
  `^--[a-z][a-z-]{0,30}=[A-Za-z0-9_.,-]{1,200}$`. It is the layout lint behind the engine's `CheckLayout` tool and is
  advisory only (S-82, S-83). A customer runner shares this allowlist, so it must admit this line too.

Package installation is not an op in v1, because the runtime container has no network egress. The runtime image must carry the dependencies compositions import.

Container requirements: read-only root filesystem, all capabilities dropped, `no-new-privileges`, non-root user, an internal network with no route out, and pid, memory and CPU limits. Model-authored code is hostile and the container is the only containment; the allowlist above is input hygiene, not isolation, because `npm run build` runs a caller-written `package.json`.

## Media session ops and argv validation

These ops are valid in `video.compile` and `video.analyze.extract` sessions. Every path is a session-relative **virtual path** inside a per-session directory; the worker maps it to a real directory it creates and deletes.

| Op | `args` | `result` |
|---|---|---|
| `stage` | `url`, `path`, `maxBytes`, `sha256` (optional) | `{bytes, sha256}` |
| `ffmpeg` | `argv` (array of strings, not including the program), `timeoutSec` | `{exitCode, stderrTail, durationMs}`; ffmpeg progress is reported through `progress` messages |
| `ffprobe` | `argv`, `timeoutSec` | `{exitCode, stdout, stderrTail}`; `stdout` is capped at 4 MiB inline |
| `read` | `path`, `maxBytes` (at most 2.75 MiB raw) | `{content, encoding, bytes}`. Binary content travels base64 (4/3 larger), so the safe raw cap for an inline read is 2.75 MiB (`MediaSessionOps.SafeInlineReadBytes`), which keeps the encoded result inside one 4 MiB payload. The agent answers `file_too_large` at once when the encoded result would not fit. The engine stats first and reads a larger file by `publish` then fetch from object storage, bounded by the caller's `maxBytes` (default `MediaSessionOps.MaxEngineReadBytes`, 256 MiB). |
| `publish` | `path`, `putUrl`, `maxBytes`, `contentType` | `{bytes, sha256}` |
| `stat` | `path` | `{exists, bytes}` |
| `close` | none | `{}` |

**The worker MUST validate every `argv` itself**, even though the engine builds it from typed, first-party code. The worker does not trust the gateway or the engine for safety, because a bug anywhere upstream must not let argv reach a customer's machine with an escape. The validator MUST reject, with error `argv_not_allowed`:

- any input that is a URL or carries a protocol prefix (`http:`, `https:`, `ftp:`, `tcp:`, `udp:`, `rtmp:`, `concat:`, `subfile:`, `file:`, `data:` and any other `scheme:` form) in `-i` or any input-like option;
- any absolute path, any path containing `..`, and any path that resolves outside the session directory;
- filter options that read files or the network from within a filtergraph (`movie=`, `amovie=`, `subtitles=`, `ass=`, `textfile=`, `fontfile=`, `lut3d=` and similar) unless the referenced file is a session-relative path that already exists in the session directory;
- `-f lavfi` or `-f concat` with an input that names anything outside the session directory;
- options that execute or write elsewhere (`-filter_script`, `-passlogfile`, `-report`, `-dump_attachment`) unless their target is session-relative.

The worker MUST add `-protocol_whitelist file,pipe` to every ffmpeg and ffprobe invocation, MUST run them with no shell, and MUST enforce `timeoutSec` and a per-session disk quota. The full validator rules are tested by D4b with fixtures such as `movie=/etc/passwd`, `-i http://example.com/x`, and `concat:` with absolute paths, all of which MUST be rejected.

ffmpeg runs natively on the host when the runner has no container runtime, and inside the runtime container when it does. Either way it only ever sees the user's own media plus filter specifications authored by first-party code.

## Job lifecycle, leases and fencing

States of a job: `Queued`, `Assigned`, `Running`, then exactly one terminal state of `Succeeded`, `Failed`, `Cancelled` or `Expired`.

Transitions:

1. The engine creates the job through the gateway's internal API; it is `Queued`.
2. A runner sends `ready{freeSlots}`. The gateway claims an eligible job transactionally, increments `lease_epoch`, sets `lease_expires_at` to now plus 45 seconds, mints the job token, sets `Assigned` and sends `assign`.
3. The runner sends `accept` within 10 seconds (the job becomes `Running`) or `reject` (the job returns to `Queued`; the attempt count is not incremented; the gateway may remember the rejection to avoid immediately re-offering it to the same runner). No answer in 10 seconds is treated as a reject and the lease is released.
4. While running, every `heartbeat` from the runner lists its held jobs with their epochs; each valid listing renews that job's lease to now plus 45 seconds. The gateway's heartbeat answer reports any lease it no longer honours with `ok:false`, and the runner MUST stop that job.
5. The job ends with `complete` (outcome `succeeded` or `cancelled`), `fail`, or by the engine closing the session.

**Fencing.** `lease_epoch` increases on every assignment of a job. The gateway MUST reject any `accept`, `progress`, `op.response`, `complete` or `fail` whose `leaseEpoch` differs from the job's current epoch, or whose `jobToken` is invalid or expired, and MUST tell that runner to stop with a `cancel` (reason `lease_lost`). This stops a slow or sleeping runner whose lease was reassigned from overwriting the new owner's result.

**Job token.** HS256 JWT signed by a gateway-held key, claims `{jobId, sessionId?, epoch, orgId, exp}`, with `exp` no later than job timeout plus 5 minutes. It is required on every job-scoped runner message.

**Liveness.** A connection with no message from either side for 45 seconds is dead: the gateway closes it with `4408`. Heartbeats are sent every 15 seconds by the runner. Messages other than `heartbeat` also count as liveness.

**Lease expiry (reaper).** A reaper runs every 10 seconds. For a job in `Assigned` or `Running` whose lease expired:

- a one-shot job is requeued while `attempt < maxAttempts` (default 2), otherwise `Failed` with class `platform`;
- a session job goes to `Expired` and the engine is told `session_lost` on its next op or long-poll; the engine decides whether to open a new session (with journal hydration for Remotion) or fail the step.

The reaper also cancels jobs whose workflow execution is no longer `Running`.

**Reconnect and resume.** A runner that reconnects while holding jobs lists them in `hello.running`. If a job's lease has not expired and the epoch matches, the gateway keeps it, answers with `welcome.resumed`, and re-sends any `op.request` that had no response; the worker returns the cached response for an `opId` it already executed. Jobs not resumed receive `cancel` and the runner MUST stop them. A laptop that slept past the 45-second lease therefore loses its session, which is why the engine keeps a workspace journal.

**Revocation.** When a device is revoked, the gateway sends `cancel` (reason `revoked`) for each of its jobs, sends `bye`, closes with `4401`, and handles the jobs as lease expiry.

**Cancellation.** Engine to gateway to `cancel` to worker. The worker MUST stop the container or process within 10 seconds (`graceSec`), clean the job directory, and send `complete{outcome:"cancelled"}`. The gateway marks the job `Cancelled` when it receives that or after 10 seconds, whichever comes first, and discards any later message with that epoch.

**Drain.** `drain` from the gateway tells a runner to take no new jobs; `drain` from the runner announces the same (for example on SIGTERM, user pause or spot reclaim). Cloud workers finish one-shot jobs within 120 seconds then send `bye`; sessions are not waited for and are rehydrated elsewhere by the engine.

## Idempotency

**Jobs.** The engine supplies `idempotencyKey = sha256(executionId|stepId|iteration|purpose)` when creating a job. Creating a job with a key that already exists returns the existing job and does not create another, so an engine retry after a timeout cannot double-enqueue.

**The one exception: a repeat may change the target of a job nobody has leased.** The key does not name the target, so a `CloudPool` job queued while no pool worker existed would otherwise stay `CloudPool` for good: the engine's retry, now routed to a desktop runner that has just connected, got the old job back, no desktop runner may claim it, and the step waited out every earlier attempt (ten minutes, in the run that found it). When a create repeats a key whose job is still `Queued` and the request names a different target, the gateway moves that one job to the new target (one `UPDATE ... WHERE status = 'Queued'`, so it cannot race a claim, which flips the status under a row lock), wakes the runners, and still answers `200` with the same job id. A job that is `Assigned` or later is returned exactly as it is, whatever the request says.

**Ops.** Every `op.request` carries an `opId` unique within its session. The worker MUST keep the results of the last 256 ops per session. A repeated `opId` MUST return the cached `op.response` without executing the op again; this makes resume after reconnect safe for ops that were already executed. An `opId` that has aged out of the cache and is repeated returns error `op_expired`, which the engine treats as session loss.

**Fencing and idempotency together.** An op executed under epoch 1 and replayed under epoch 2 is a new session on a new runner and is not deduplicated; the engine's journal replay is what restores state.

## The engine's workspace journal (WP D7b)

A runner's op cache dies with the process. It makes a retry safe on the SAME session, but it cannot tell a **new** session what the old one had written — and a `remotion.workspace-session` is held by a machine that can sleep, be closed or be reclaimed. The engine therefore keeps its own journal of every mutation, in object storage, and replays it into `hydrate` when a session has to be replaced. This section is what a runner implementer needs to know about `hydrate`'s input; the key space and the folding rules are the engine's business and are implemented in `inference/src/ReelBolt.WorkflowEngine/Services/Compute/SandboxWorkspaceJournal.cs`.

**When a session is replaced.** On `410 session_lost` — and on `op_expired`, which is the same thing from the engine's side — the engine releases the execution's session slot, re-resolves the target through `IComputeTarget` (so an organization set to `RunnerOnly` whose runner has gone away is refused with its own failure code and never silently moved to cloud billing), creates a NEW job under a DIFFERENT `purpose` (a repeated idempotency key would return the expired job), and hydrates the new workspace from the journal before retrying the failed op once. `404 session_not_found` is deliberately NOT recovered: the protocol says the engine must not retry it.

**When a session is parked.** An execution has one session slot (`runner_sessions` allows one open session per execution), so before a runner-backed `video.compile`/`video.analyze.extract` job of the same execution is created, the engine PARKS its `remotion.workspace-session`: it sends `close` (the workspace is destroyed and the gateway ends the job as `Succeeded`), reads the job until it is terminal — cancelling it and waiting out the cancel grace window when the close did not end it — and only then lets the media job be created. Nothing new is asked of a runner: a parked session is an ordinary closed one. The journal is not sealed, and the next workspace op is served by a NEW job under a new `purpose` whose workspace is rebuilt with one `hydrate`, exactly as after a session loss. See [`compute-fabric.md`](compute-fabric.md) "Parking the workspace session before a media step".

**What `hydrate` receives.** One entry per path that still exists at the end of the run — a delete removes its path and everything beneath it, and the last write of a path wins — each `{path, sha256, url}` with a presigned GET minted when the op is issued and never stored. A runner may skip a path whose digest already matches; on a fresh container nothing will.

**Key space.** `projects/{projectId}/agentFiles/sandbox-journal/{executionId}/{seq:D8}.json` holds the entry (`{sequence, path, kind, sha256, bytes, blobKey, recordedAt}`, `kind` being `write` or `delete`) and `{seq:D8}.bin` the content. The fixed-width sequence is what makes an ordinal listing the journal's order. Everything sits under the project prefix, so D2's `projects/{projectId}/` presigning boundary is what authorizes a hydrate URL and the journal cannot widen it.

**Lifecycle.** The journal spans one execution and is deleted when that execution ends — the workspace does not outlive its execution, so nothing is kept. Sequence cursors are re-derived from a listing, so a journal written by a previous engine process (an execution recovered after a restart) continues where it stopped instead of overwriting `00000000`.

**How the engine sends the byte-moving ops (WP D8).** `stage` and `publish` are the ops that move bytes, and on a runner-backed session the engine issues them with a **presigned URL and nothing else** — one object, one method, minted when the op is issued and never stored. `ISandboxSession.StageFromUrlAsync` and `PublishToUrlAsync` are the seam members, and `ISandboxSession.ResolvePublishRouteAsync` is how a caller learns where its bytes may go: on the LOCAL arm the engine transfers the bytes itself (the sandbox-executor's container has no egress) and the caller writes its final key directly, because nothing crossed a machine boundary; on a runner-backed arm the only writable object is `runner-staging/{jobId}/{n}` and the caller reaches its final key through the platform's verifier.

**The raw read is a publish.** `OpenRawFileAsync` — the engine's streaming read of a workspace file — has no inline arm on a runner: the inline cap is 4 MiB and a render is not. It publishes the file into the job's staging key space and serves the read from a presigned GET of that object, deleting the staged copy when the caller is done.

**What the engine still does NOT send over a runner.** npm package installation: v1 has no op for it, because the runtime container has no network egress, and a runner-backed session answers that call with a refusal the agent can read rather than faking it.

## Presigned URLs and object storage

Media bytes move between the runner and the object store using presigned URLs, never through the gateway and never over the socket.

- A URL grants **one object and one method**. GET URLs are used by `stage` and `hydrate`; PUT URLs by `publish` and `render`.
- The engine mints URLs **when it issues the op**, not when the job is created, and never stores them. A job that waits in the queue never holds an expired URL.
- GET URLs expire after **15 minutes**. PUT URLs expire after **30 minutes** and target only `runner-staging/{jobId}/{n}`.
- Default size caps: render 2 GiB, compile output 4 GiB, extract artefacts 256 MiB, WAV 512 MiB. The runner enforces `maxBytes` itself while streaming; the platform enforces the cap again after upload with a HEAD request, because R2 and Garage cannot sign a maximum length into a presigned PUT.
- Output lands in `runner-staging` and is **not** the delivered object. Only after the engine verifies it (ffprobe against the job spec, size cap, hash, optional spot checks) is it promoted to its final key. A failed verification deletes the staging object and fails the step with class `runner`.
- The shape probe first runs ffprobe over a presigned GET. If that fails, the engine downloads the staged object (bounded by the job's size cap) and probes the local file with only the `file` protocol; only when both fail is the output `RUNNER_OUTPUT_UNREADABLE`. The failure message and log carry the object's size and its first 16 bytes (hex) and ffprobe's complaint, never a URL: presigned URLs, including the ones ffprobe echoes in its errors, are replaced by `[staging url]` before they are logged.
- Which check applies follows the content type the engine chose for the publish, never the runner's claim. `application/json` (and `+json`) is a document: no ffprobe, instead the staged object is downloaded (bounded), must parse as one JSON object or array (nesting at most 64), and its final key must sit under `projects/{projectId}/`; otherwise `RUNNER_OUTPUT_INVALID_DOCUMENT` (runner fault, not refunded). Text sidecars (`text/*`, `application/x-subrip`, `application/x-subtitle`, `application/ttml+xml`) are never probed as media either: the staged object must be non-empty, within the size cap, strict UTF-8 with no NUL byte, and its final key under `projects/{projectId}/`; otherwise `RUNNER_OUTPUT_INVALID_DOCUMENT`. Video, audio, images and unrecognised types keep the media check.
- The runner computes SHA-256 of every object it publishes and returns it in the op result; the engine treats that value as a hint and verifies independently.
- URLs are bearer secrets for one object: they MUST be redacted in logs and MUST NOT be forwarded to any other party.
- URLs reference the user's own project objects only. No presigned URL can ever name a credential store, another organisation's object, or a model provider endpoint.

## Routing and targets

A job has a `target`: `CloudPool` or `OrgRunner`, and an `org_id`. The engine's selector (not the gateway) chooses the target using the organisation's compute preference and entitlements.

- A Desktop runner MUST receive only jobs where `job.org_id == device.org_id` and `target == OrgRunner`.
- A CloudPool runner MUST receive only jobs with `target == CloudPool`, for any organisation.
- Jobs are never cross-assigned: an `OrgRunner` job never falls through to the pool inside the gateway. If the engine wants a fallback it either repeats the create with the same key and the other target while the job is still `Queued` (the gateway retargets it, see "Idempotency"), or cancels the job and creates a new one under a new idempotency key.
- Eligible jobs are claimed with `SELECT ... FOR UPDATE SKIP LOCKED` ordered by priority then creation time, filtered by target, organisation and the capability requirements.
- A job runs only under an active plan: the engine creates jobs only for organisations whose entitlements allow compute, and the gateway never creates jobs by itself.
- Fair share: a per-organisation cap on concurrent CloudPool slots applies at claim time.

## Error and failure classes

`fail.class` tells the engine whose fault the failure was, which decides whether the customer is refunded credits and whether the step is retried.

| `class` | Meaning | Credit effect | Engine retry |
|---|---|---|---|
| `platform` | Our infrastructure failed (disk full on a pool worker, lost lease, gateway restart). | Refunded automatically. | Engine may retry on CloudPool. |
| `runner` | The runner misbehaved or its output failed verification (corrupt file, duration mismatch, container crash on a desktop). | Not refunded. | Per `retryable`; may fall back to CloudPool if entitlements allow. |
| `input` | The job's input was bad (media that ffmpeg cannot read, a composition that does not build). | Not refunded. | `retryable` is false; surfaced to the agent or user. |

`fail.retryable` is a hint. The engine decides. A runner MUST NOT set `class: "platform"` to obtain a refund: the class that counts for billing is the one the platform derives itself (lease expiry and verification are platform-observed), not the one the runner claims. A runner-supplied `platform` class is recorded as a claim only.

Op-level errors (`op.response.error.code`) include: `invalid_path`, `argv_not_allowed`, `exec_not_allowed`, `file_too_large`, `output_too_large`, `unsupported_op`, `op_expired`, `op_in_flight`, `stage_failed`, `publish_failed`, `timeout`, `container_unavailable`, `disk_full`, `cancelled`.

## Security invariants

These hold for every message, op, fixture and implementation. They are the review checklist for the gateway, the worker library, the engine client and the desktop app.

**No message or op ever carries a provider credential.** No field in any message in this specification can hold an API key, token, endpoint secret or `env:` reference for an inference, transcription, vision, embedding, speech or video-generation provider. Provider resolution, and every LLM, ASR, vision and embedding call, happen in the engine and Inference API processes. A job spec contains only what the job needs to run: file paths, argv, composition ids, sizes, object keys in the user's own project. The same holds for platform secrets in general: no database credential, signing key, service token or other organisation's data is ever assigned. The desktop runner's E14 review tests this by fuzzing assigned jobs and ops for credential-shaped material.

**Runner output is untrusted.** The runner binary is controlled by its owner and may be modified. Therefore: output objects are verified by the engine before promotion; results are tagged `FromRunner` and are excluded from the step-result cache; text returned by ops (`readFile`, `read`, `exec` output, `status`) is treated as data and passes through the same handling as any tool output, never as instructions to the platform; a device key proves which device sent output, not that the output is honest; capability claims, metrics and `fail.class` are claims, not facts.

**Billing is priced from the job spec.** Credits for compute are reserved and settled from the platform-side job record: job type, requested resolution and duration, step configuration. Nothing in `complete.result`, `complete.metrics`, `progress` or any runner-reported timing, size or count changes what a customer is charged. A desktop runner costs 0 compute credits (or a configured fraction) by policy; that policy is applied by the engine.

**The worker distrusts its upstream.** The worker validates paths, `exec` commands and ffmpeg argv itself and refuses job fields that look like host paths. It never mounts anything outside its own scratch root and never follows a presigned URL it was not handed in an op.

**Least authority per credential.** A stolen device identity can pull only jobs of its own organisation and PUT only to URLs in those jobs. A stolen job token covers one job at one epoch. A leaked pool credential can pull CloudPool jobs and is rotatable. None reaches the API, the database, another organisation or any provider key.

**No obfuscation.** The protocol makes no attempt to hide anything from the runner's owner; the value is server-side (active plan gating, verification, revocation).

## Gateway internal API (engine-facing)

The gateway's internal surface is how the engine creates work and runs the ops of a session. It is
**not public**: every route requires the shared internal token in `Authorization: Bearer …`, and a
gateway with no token configured answers **503** to all of them rather than serving them openly —
these routes create and cancel work on other people's runners and describe the fleet.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/internal/v1/jobs` | Create a job. Idempotent on `sha256(executionId\|stepId\|iteration\|purpose)`: a repeated key returns the existing job and creates nothing. |
| `GET` | `/internal/v1/jobs/{id}?wait=30s` | Read a job, optionally long-polling until it reaches a terminal state. The answer carries `sessionId` once the job has one. |
| `DELETE` | `/internal/v1/jobs/{id}` | Cancel a job. A queued job becomes `Cancelled` at once; a leased one when the runner acknowledges it or after `graceSec`, whichever comes first. |
| `GET` | `/internal/v1/runners?org=&caps=` | Which runners are online on the replica that answers. Per replica, so a `no` is a hint and never a routing decision. |
| `POST` | `/internal/v1/sessions/{id}/ops` | Run one op on a session and wait for its result. |
| `GET` | `/internal/v1/scaling` | The desired CloudPool slot count. |
| `POST` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/chat/completions` | Relay one OpenAI chat request to the user's local model server through that device's runner; streams the answer. |
| `POST` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/embeddings` | Relay one OpenAI embeddings request the same way. |
| `GET` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/models` | Relay a model-list request the same way. |

The three `llm` routes (chat, models, embeddings) carry no provider credential: the engine and Inference API send a model request body only, and the target is the organisation's own paired device. Their error table is in [`local-llm-gateway.md`](local-llm-gateway.md#gateway-internal-routes).

### Session ops

`POST /internal/v1/sessions/{id}/ops` takes `{opId?, op, args?, timeoutSec?}` and answers with the
runner's `op.response` passed through: **200** with `{ok: true, result}` or `{ok: false, error}`
(a failed op is a successful round trip — only the transport failing is an HTTP error). The gateway
mints an `opId` when the engine does not supply one. `timeoutSec` defaults to the job's own timeout,
because an `exec` or a `render` may legitimately run that long.

A session is pinned to one gateway pod by `runner_sessions.gateway_instance`, which is written when
the job is assigned. A replica that does not hold the session forwards the request **once** to the pod
the column names, over the headless service, and passes the answer back verbatim; there is no shared
cache and no Redis. A replica that holds the runner's **live socket** serves the op itself even when
the column names another pod, because a runner that reconnected elsewhere is a fact the column has not
caught up with.

Failures are distinguishable, and the difference matters to the engine:

- **404 `session_not_found`** — no such session was ever opened. The engine must not retry it.
- **410 `session_lost`** — the session existed and cannot serve an op: it is closed, its runner is not
  connected to any replica, or its lease is gone. Open a new session (with journal hydration for
  Remotion) or fail the step. The reason is named in `reason`.
- **409 `op_in_flight`** — the session already has an op running. `cancel` is the only message allowed
  while an op runs.
- **409 `op_cancelled`** — the job was cancelled, or the execution ended, while this op was running.
- **504 `op_timeout`** — the runner did not answer within the deadline. The session may still be
  running the op; a retry with the same `opId` is safe, because the worker answers a repeated `opId`
  from its own cache.
- **502 `session_owner_unreachable`** — the replica named by `gateway_instance` could not be reached.

**Cancellation.** Cancelling a job (through this API or through revocation, lease expiry or execution
end) sends `cancel` to the runner, which must stop the container within `graceSec`, and releases any
op the engine is waiting on immediately rather than leaving it to time out.

**Reconnect.** A runner that reconnects inside its lease is listed in `welcome.resumed`, and any
`op.request` that had no response is sent again, with the same `opId`. The worker's op cache is what
makes the repeat safe.

### Scaling signal

`GET /internal/v1/scaling` returns `desiredCloudPoolSlots` = **queued + running CloudPool jobs + a
configurable buffer**, which is what a pool autoscaler sets its replica count from, along with
`queued`, `running`, `buffer`, `connectedCloudPool`, `availableCloudPool`,
`freeSlotsCloudPool`, `freeSlotsOrgRunner`, `runnersWithoutJobTypes` and `drainingRunners`.

**A connected runner is not automatically available capacity.** A runner with no ffmpeg and no
container runtime advertises an empty `jobTypes` list and still connects; it appears in
`connectedCloudPool` and in `reelbolt_runner_connected{kind}`, but it contributes no free slots and
is counted in `runnersWithoutJobTypes` instead. A draining runner is likewise excluded from free
slots and counted in `drainingRunners`. The only number that means "this could take work right now"
is the free-slot figure, and it is **per replica**: the live-runner registry is in memory by design,
so `freeSlotsCloudPool` is one pod's view and a fleet-wide number comes from the metric.

Three Prometheus series carry the signal: `reelbolt_runner_queue_depth{target,job_type}` (queued jobs,
read from `runner_jobs` on each reaper pass), `reelbolt_runner_free_slots{target}` and
`reelbolt_runner_connected{kind}`.

## Versioning and compatibility

The envelope field `v` is the **major** version. `hello.protocolVersion` is `"major.minor"`. The gateway accepts a runner whose major equals one it supports and closes others with `4400`, sending first (when it can) a `bye` whose `reason` names the supported versions. Within a major version, changes are additive only: new optional fields, new message types (ignored by old peers), new ops (answered `unsupported_op`), new `jobTypes` and new capability fields. A change that removes or reinterprets a field requires a new major version and a parallel `/v2/connect`.

`welcome.protocolVersion` is the version the gateway will speak on this connection (the highest minor both sides support). The gateway MAY include `minAgentVersion` in `welcome`; a runner older than that SHOULD show a clear update message and MUST NOT take jobs.

Enum-like string values (op names, reject reasons, job types, error codes) are open sets: an unrecognised value MUST be handled as an unknown case, not as a parse error.

## Fixtures and contract tests

Golden messages for every message type are kept in `sandbox/protocol/fixtures/`, one JSON object per file, named `<type>.<variant>.json` (for example `hello.desktop.json`, `op.request.exec.json`). Each is a complete envelope with `v`, `type`, `id` and `payload`, plus `jobId`/`sessionId` where the type is job- or session-scoped.

Rules for fixtures:

- Every message type in the catalogue has at least one fixture; a Go test in the `sandbox` module fails when a type has none, when a file is not valid JSON, or when the required envelope fields are missing or the file name's type prefix disagrees with the `type` field.
- Fixtures contain only obviously fake values. Presigned URLs use the reserved `.invalid` top-level domain and a fixture signature; job tokens are the literal placeholder `FIXTURE.job.token`; no fixture contains anything that looks like a real credential.
- The gateway (D5a), the worker library (D4b) and the .NET engine client (D6) add contract tests that round-trip every fixture through their own typed models. Adding a message type or op means adding a fixture first.
- The initial Go test only checks the minimal envelope. Full typed protocol structs arrive in `sandbox/pkg/protocol` with D5a.
