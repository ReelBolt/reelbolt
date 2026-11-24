# ADR: Runner protocol (work package D1)

- **Status:** Proposed (accepted when D1 merges; the E0 and D-area owners sign off on sections 3, 5 and 6)
- **Date:** 2026-10-05
- **Decides:** how the platform hands compute work (Remotion renders, VideoCompile, VideoAnalyze extraction) to a machine it does not run inside its own process: our cloud worker pool and a customer's desktop runner.
- **Spec:** [`docs/runner-protocol.md`](../runner-protocol.md) is the normative specification. This ADR records *why*. If they disagree, the spec wins for wire details and this ADR wins for intent.
- **Builds on:** the earlier SaaS launch decisions on the runner pool, the gateway, device pairing and metering.

## 1. Context

Today the engine talks to `sandbox-executor` over HTTP (`/api/v1/sandboxes/{executionId}/...`, about 17 call sites in `ReactRemotionSandboxTools.cs`) and runs ffmpeg/ffprobe in-process inside the `workflow-engine` container (`VideoAnalyzeStepExecutor.cs`, 4,534 lines; `VideoCompileStepExecutor.cs`, 9,480 lines). That cannot scale out and cannot reach a customer's own hardware. The launch plan needs two new places to run the same work:

1. **CloudPool**: elastic worker VMs we operate (scale to zero).
2. **OrgRunner**: a desktop app owned by a customer, behind a home or office NAT, that lends its CPU/GPU to that customer's own jobs.

Constraints that shaped the decision:

- Desktop machines accept no inbound connections, so every connection is outbound from the runner.
- The same worker library must serve both targets (D13); the desktop app only adds tray UI, pairing and an updater (Area E).
- Provider API keys never leave the server (D3). Model calls stay in the engine process.
- Runner output cannot be trusted (D16): a customer controls the binary.
- `docker compose up` for self-hosters stays zero-config (D18), so the protocol is additive and `Compute:Mode=Local` remains the default.
- Laptops sleep, lose Wi-Fi and get closed mid-job.

## 2. Decision summary

| # | Decision |
|---|---|
| 1 | One **outbound WebSocket** from runner to a new Go `runner-gateway`, JSON envelope `{v, type, id, jobId?, sessionId?, payload}`, 15 message types, inline payload at most 4 MiB, **binary data never on the socket**. |
| 2 | Binary data moves **only through single-object presigned URLs** (GET to stage, PUT to publish). |
| 3 | Work is modelled as **sessions with ops**, not one-shot plans, for all three v1 job types. |
| 4 | **Leases with fencing epochs**, heartbeat-renewed; the queue is Postgres, not RabbitMQ. |
| 5 | **Idempotency** by engine-supplied key (jobs) and `opId` (ops, with a 256-entry result cache on the worker). |
| 6 | **Auth:** Ed25519 device keys for desktop, a pool credential for cloud, an HS256 per-job token on every job-scoped message. |
| 7 | The protocol contains **no operation, field or message that carries a provider credential.** |
| 8 | Runner output is **untrusted**; billing is **priced from the platform-side job spec**, never from anything the runner reports. |

## 3. Why sessions rather than one-shot job plans

The tempting design is a declarative plan: the engine computes the full ffmpeg argv list up front, ships it as one job, and gets results back. We rejected it for media work because the executors are interleaved with their own results:

- `VideoCompileStepExecutor` probes files after producing them (`IMediaProbe.ProbeAsync` is called at roughly ten sites, for example once per encoded segment near line 6172 and again for generated clips near line 6547) and the next argv depends on the probe result.
- `VideoAnalyzeStepExecutor` chooses which shots to caption, and which keyframes to extract, from C# frame-grid analysis (`FrameGridAnalyzer`) of an earlier ffmpeg pass.
- Precomputing a plan would mean rewriting roughly 14,000 lines of executor logic into a plan compiler plus an interpreter, with every conditional re-expressed as data.
- The Remotion path is already conversational: an agent writes files, runs `npm run build`, reads errors and iterates (`ReactRemotionSandboxTools.cs`).

A **session** keeps the control flow where it already lives (the engine) and moves only the *execution* of each primitive call to the runner. The cost is chattiness: one round trip per op. A compile is expected to issue tens of ops, not thousands, and each op is cheap relative to an encode, so we accept it (D11 logs op counts to verify). It also makes the `IMediaWorkspace` seam (D9a) the natural integration point: each workspace method maps to one op.

The protocol still specifies one-shot job lifecycle rules (requeue on lease expiry) because future job types may be one-shot, but **v1 defines no one-shot job type**.

## 4. Why a Go gateway with a Postgres queue

- Gateway in Go inside the `sandbox` module: the worker and desktop agent are Go, so one `sandbox/pkg/protocol` package defines the wire types for gateway, worker and app; one goroutine per socket is cheap for thousands of mostly idle connections; the CI matrix already covers the module.
- Postgres, not RabbitMQ, as the queue: matching has to filter by org, capability and target; leases and fencing need transactional updates; queue depth is one SQL query. RabbitMQ could only express "only this org's runner may take this message" with a queue per org. RabbitMQ stays the transport for domain events (for example `RunnerDeviceRevoked`).
- Op routing across gateway replicas by pod IP over a headless service, no Redis (`runner_sessions.gateway_instance`).

## 5. Authentication decisions

- **Desktop:** per-device Ed25519 key pair created at pairing (Area E4). The gateway verifies a signature in `hello` against `runner_devices.public_key` and checks `revoked_at`. `runner_devices` is owned by D3 (ruling R1).
- **Deviation from the plan text, with reason:** the plan says the runner signs "the `welcome` nonce", but the runner speaks first in the specified message order (`hello` then `welcome`), so a server nonce does not exist yet when `hello` is sent. Rather than add a challenge message, `hello` carries a client nonce and timestamp and signs `reelbolt-runner-hello-v1 | deviceId | ts | nonce | gatewayHost`, with a ±120 s skew window and a replay cache. This is the same construction E4b specifies for `POST /api/v1/runners/token`, so there is one verification routine, and binding the gateway host stops a hostile gateway replaying the hello at another one.
- **Cloud pool:** a shared pool credential from the secrets manager, `kind=CloudPool`, `OrganizationId=null`. A leaked pool credential can pull CloudPool jobs of any org, which is why it lives only on our worker VMs and is rotatable without a protocol change.
- **Per-job token:** HS256 JWT minted by the gateway at `assign`, claims `{jobId, sessionId?, epoch, orgId, exp}`, echoed on every job-scoped runner message. A stolen device identity cannot speak for a job it was not assigned, and a stale epoch is rejected.
- **Presigned URLs travel inside ops**, minted when the engine issues the op, not when the job is created. A job that waits 20 minutes in the queue therefore never holds an expired URL, and a URL never sits at rest in `runner_jobs.spec_json`. (D13 and the plan text say "jobs carry presigned URLs"; in practice the URLs ride on the ops of a job.)
- **Engine to gateway:** cluster-internal API behind network policy plus a service token.

## 6. Trust model (the part reviewers should read)

Three rules are stated normatively in the spec and tested elsewhere (E14, D12, D5c):

1. **No credential for any model provider ever crosses this protocol.** There is no op that carries one, no field in any message that can, and provider resolution plus every LLM, ASR, vision and embedding call remain in the engine and Inference API processes (D3, D14, R11). VideoAnalyze is split precisely so that ASR and vision captioning stay platform-side. A reviewer verifies this by reading the schema for credential-shaped fields and by the E14 test that fuzzes assigned jobs for key material.
2. **Runner output is untrusted.** A modified binary can return anything. Output bytes are verified by the engine (ffprobe against the job spec, size cap, hash, optional spot checks; D12), tagged `FromRunner`, and excluded from the step cache. Text results (`readFile`, `read`, `status`, exec stdout) are treated as model-visible *input data* like any other tool output and never as instructions to the platform.
3. **Billing is priced from the job spec.** Credits are reserved and settled from what the engine asked for (job type, duration estimate, resolution, step config), never from `complete.result`, never from runner-reported metrics. A runner that under- or over-reports changes nothing the customer is charged.

Consequence of the "no DRM" stance (D16): the protocol hides nothing from the runner owner. They see their own media, the model-authored Remotion source and the presigned URLs for their own objects. A cracked runner gains nothing it was not already sent.

The reverse direction (platform to user) is covered by the desktop runner ADR: model-authored Remotion code is hostile, so it runs only inside the no-egress container, ffmpeg argv is validated by the worker again (the worker does not trust the gateway either), and job fields that look like host paths are rejected.

## 7. Consequences

- All of D5 (gateway), D4b (worker library), D6/D7/D9/D11 (engine) and E5 (desktop) code against one document and one fixture set. Fixtures live in `sandbox/protocol/fixtures` and are used by Go and .NET contract tests.
- The protocol is chatty by design. If op counts prove too high on a real compile, the mitigation is batching ops (a future additive message), not abandoning sessions.
- Sessions pin a workflow execution to one machine, so a lost runner means session loss. D7b's workspace journal and re-open/hydrate logic are therefore load-bearing, not optional.
- Adding a second ffmpeg-class capability later (for example hardware encode flags) touches only the worker's argv validator and the `hello` capability list.
- `Compute:Mode=Local` is untouched; self-hosters can ignore everything here.

## 8. Alternatives rejected

| Alternative | Why not |
|---|---|
| Declarative one-shot job plans for media | Requires rewriting about 14k lines of interleaved executor logic into a plan compiler (section 3). |
| gRPC or HTTP/2 bidirectional streaming | Harder through consumer proxies and corporate TLS inspection; WebSocket plus JSON is adequate at our message rates. |
| Runner polls an HTTP job queue | Adds latency to interactive Remotion sessions (an agent waits on every op) and makes cancellation slow. |
| Inbound connections to the desktop (tunnels, port forwarding) | Violates the outbound-only rule and opens a port on customer machines. |
| RabbitMQ as the job queue | Cannot express per-org eligibility without a queue per org; no transactional lease and fence (section 4). |
| Redis for cross-replica session routing | One more stateful dependency; pod-IP proxying over a headless service needs none. |
| Server nonce in `welcome` signed by the runner | Message order makes it impossible without an extra round trip (section 5). |
| Binary frames over the socket | Large media in the control channel stalls heartbeats; presigned URLs let the object store carry the bytes. |
| Letting the runner call model providers for lower latency | Would put provider keys on customer machines. Rejected outright by D3. |
| Obfuscation or attestation of the runner binary | Does not stop a determined owner and adds support cost; the controls that matter are server-side (D16). |

## 9. Open questions

1. **Hello signature scheme versus E4b.** The gateway verifies the device signature itself, so the 15-minute runner JWT from `POST /api/v1/runners/token` (E4b step 4) may be unnecessary for the websocket. Does anything else need that token? Decide in E4b; the spec does not depend on it.
2. **Package installs on a runner.** `POST /packages` in the Local sandbox needs egress, and runners have none. v1 ops include no `installPackages`; the runtime image must carry every dependency a model-authored composition may import. Acceptable, or do we need an allowlisted npm mirror?
3. **Session idle and lifetime defaults.** The spec proposes an idle timeout of 30 minutes and a lifetime equal to the job timeout (cap 4 h). Confirm against real Author-agent run lengths.
4. **Sampled output verification percentage** for OrgRunner outputs (D12 config). Left open.
5. **Signed job envelope** (E0 item 5, compromised gateway). v1 relies on TLS pinning in the desktop build plus per-job tokens; a gateway-signed job body is a compatible later addition (a new optional `sig` field).
6. **Reconnect resume of in-flight ops.** The spec re-sends unanswered ops after a resume within the lease window and relies on the worker's 256-entry op cache. Confirm 256 is enough for long compiles.
7. **Hardware-encoder capability** (VideoToolbox, `h264_mf`, VAAPI) is deliberately not in v1 `hello`; add `encoders[]` when E0's follow-up lands.
8. **Gateway signing key rotation** for job tokens (dual-key window) is not specified; F-area owns secrets handling.
