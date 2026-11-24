# Local-LLM gateway

The local-LLM gateway lets a workspace use a language model that runs on a member's own computer (Ollama, LM Studio, llama.cpp, vLLM or any server that speaks the OpenAI chat API). ReelBolt does not host or run that model: the platform only relays requests to it through the desktop runner's existing outbound WebSocket. This is internal background and is not published. The customer page is [`user-guide/account/local-model.md`](user-guide/account/local-model.md); the wire protocol is in [`runner-protocol.md`](runner-protocol.md).

## Architecture

```text
engine / Inference API --OpenAI HTTP--> runner-gateway (internal route, bearer token)
   --WebSocket: llm.request--> desktop app --HTTP, address from its own settings--> user's model server
   <--WebSocket: llm.response.start / chunk / end (or llm.error)--
```

- The .NET `ChatClientFactory` builds the usual OpenAI chat client for a `LocalRunner` provider row, with the base address `{Compute:GatewayBaseUrl}/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1`. The OpenAI wire format passes through unchanged, so streaming and tool calls work as they do for any provider.
- The gateway holds the runner's socket. It looks the device up, checks it, writes one `llm.request` and streams the answer back to the HTTP caller as it arrives (with `X-Accel-Buffering: no`).
- The desktop app opens no inbound port. The target URL comes from the app's own settings, never from a message.

## Wire messages

Six message types, all additive (older peers ignore unknown types) and not job scoped: no `jobId`, `leaseEpoch` or `jobToken`. Go types are in `sandbox/pkg/protocol/llm.go`.

| Type | Direction | Payload |
|---|---|---|
| `llm.request` | G to R | `requestId`, `method` (`POST` or `GET`), `path`, `body` (JSON text, at most 3 MiB), `stream`, `timeoutSec` (0 to 900) |
| `llm.response.start` | R to G | `requestId`, `status`, `contentType` |
| `llm.response.chunk` | R to G | `requestId`, `seq`, `data` (UTF-8 text, at most 256 KiB; SSE lines pass through verbatim) |
| `llm.response.end` | R to G | `requestId`, `bytes` |
| `llm.error` | R to G | `requestId`, `code`, `message` |
| `llm.cancel` | G to R | `requestId` |

The runner serves a fixed path allowlist and nothing else:

| Method | Path |
|---|---|
| `POST` | `/v1/chat/completions` |
| `POST` | `/v1/embeddings` |
| `GET` | `/v1/models` |

Any other method or path is answered with `llm.error` `unsupported_path`. No query string, host or header crosses the wire. `llm.error` codes: `disabled`, `unsupported_path`, `busy`, `server_unreachable`, `timeout`, `too_large`, `cancelled`, `bad_request`.

`hello.capabilities.localLlm` is `{enabled, serverKind, models, maxConcurrent}`: at most 100 model names of at most 200 characters, `serverKind` at most 64 characters, `maxConcurrent` at most 64. It is self-reported and never carries a URL or credential. See [`runner-protocol.md`](runner-protocol.md#capabilities).

## Gateway internal routes

Both routes need `Authorization: Bearer <RUNNER_INTERNAL_TOKEN>`; a gateway with no token answers `503 internal_api_disabled`.

| Method | Path |
|---|---|
| `POST` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/chat/completions` |
| `POST` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/embeddings` |
| `GET` | `/internal/v1/llm/orgs/{orgId}/devices/{deviceId}/v1/models` |

The gateway routes only to a connected Desktop runner whose `org_id` equals `{orgId}`, that is not revoked, and whose `capabilities.localLlm.enabled` is true. Request bodies are capped at 3 MiB in and 32 MiB out (64 MiB out for embeddings); at most 16 requests per runner are in flight at the gateway. The default per-request deadline is five minutes. Errors are JSON `{"error":{"code","message"}}`.

| Status | Code | Meaning |
|---|---|---|
| 400 | `bad_request` | Body is not UTF-8 JSON, or not a JSON object |
| 404 | `device_not_found` | No such device in this organisation (also returned for another organisation's device) |
| 409 | `local_llm_disabled` | The runner is no longer paired, or does not share a local model |
| 413 | `request_too_large` | Body over 3 MiB |
| 429 | `busy` | Too many requests in flight for this runner, or the runner answered `busy` |
| 502 | `local_server_unreachable` | Runner answered `server_unreachable` (also when the server tried to redirect) |
| 502 | `local_response_too_large` | Runner answered `too_large` |
| 502 | `cancelled` | Runner answered `cancelled` |
| 502 | `runner_rejected_request` | Other runner `llm.error`, or an invalid runner response |
| 502 | `runner_connection_lost` | The socket failed or closed before the answer finished |
| 503 | `runner_offline` | The runner is not connected to this gateway replica and no peer replica holds it (or peer forwarding is off) |
| 503 | `registry_unavailable` | The device registry could not be read |
| 503 | `internal_api_disabled` | The gateway has no internal token |
| 504 | `timeout` | The local model did not answer in time |

Every refusal the gateway makes itself carries the response header `X-ReelBolt-Relay-Error: <code>`. An error answer from the user's own model server (for example a 404 for a model that is not installed) is relayed verbatim, with its own status and body and without that header, so the .NET client can tell the two apart: a gateway refusal is explained as a connection problem, a server answer is reported as the server's own message (cleaned and cut to 300 characters).

The gateway never logs or stores a body. The per-request log line and the metrics carry ids, sizes and a result label only.

## The LocalRunner provider kind

`InferenceProviderKind.LocalRunner` is an append-only enum value. Rules (`LocalRunnerProviderRules`, `InferenceProvidersController`):

- Organisation-owned only. The platform-wide `/api/v1/inference-providers` refuses the kind.
- `Endpoint` is the paired device id (a GUID). It must name a non-revoked Desktop device of the row's own organisation; another organisation's device reads exactly like a missing one. The stored form is the canonical `D` format.
- No API key. A key of any kind is refused.
- Capabilities: Chat, Vision and Embedding (`EmbeddingClientFactory`, `LocalRunnerEmbeddingGenerator`); after switching a workspace's embedding model, reindex (see [`embeddings.md`](embeddings.md)); every other combination is refused.
- Models are the names the user's server reports.
- Not metered and not charged: 0 credits, no ledger rows, like other bring-your-own providers.
- It never falls back to a cloud model. A gateway refusal, a timeout or a network failure raises `LocalRunnerUnavailableException` (code `LOCAL_RUNNER_UNAVAILABLE`) with a plain-language reason, and the engine classifies it as `FailureClass.ByoProviderError`. Retries are off for relayed calls.
- `GET /api/v1/org/runner-llm` (Owner/Admin) lists the workspace's non-revoked desktop apps that offer a local model, for the provider form. It returns only the fields the form needs.

## Configuration

Set the same two keys in both the Inference API and the engine (the Inference API needs them for the provider test and the assistant):

| Key | Default | Meaning |
|---|---|---|
| `Compute:GatewayBaseUrl` | `http://runner-gateway:8090` | Where the gateway's internal API is |
| `Compute:InternalToken` or env `RUNNER_INTERNAL_TOKEN` | none | Shared bearer token, equal to the gateway's |

When the Inference API and the gateway run on different hosts (the two-box deployment), the link between them is TLS 1.3 with mutual authentication. Extra keys, needed only where the caller reaches the gateway across hosts (the Inference API on the control box). The engine on the media box reaches the gateway over its compose network and leaves them unset:

| Key | Default | Meaning |
|---|---|---|
| `Compute:GatewayCaFile` | none | PEM file with the CA that signed the gateway's certificate. When set, this CA is the only trust root for the gateway link (system roots are not consulted) |
| `Compute:GatewayClientCertFile`, `Compute:GatewayClientKeyFile` | none | PEM client certificate and key presented to the gateway (mutual TLS); `Compute:GatewayClientKeyPassword` for an encrypted key (config or env only) |
| `Compute:GatewayRequireTls` | `false` | When `true`, host startup fails unless `GatewayBaseUrl` is `https` and `GatewayCaFile` is set; at run time a plain URL is refused with the "not secured" reason, never a fallback. Any other TLS key set with an `http` URL, or only one of the client certificate and key, also stops startup. Files are not required at startup; a missing file fails the call with a typed error |

Gateway side (all empty or false by default, so a single-host install is unchanged):

| Env | Meaning |
|---|---|
| `RUNNER_GATEWAY_INTERNAL_TLS_ADDR` | Address of an extra listener that serves only `/internal/v1/**` (and a bare `/healthz`) over TLS 1.3, for example `:8443`. Not the runner websocket, not metrics |
| `RUNNER_GATEWAY_INTERNAL_TLS_CERT_FILE`, `RUNNER_GATEWAY_INTERNAL_TLS_KEY_FILE` | Server certificate and key (both required when the address is set); re-read when the files change, at most every 30 seconds, so rotation needs no restart of the gateway |
| `RUNNER_GATEWAY_INTERNAL_TLS_CLIENT_CA_FILE` | CA bundle for client certificates; a valid client certificate is required. Startup fails when the address is set without it, unless `RUNNER_GATEWAY_INTERNAL_TLS_ALLOW_NO_CLIENT_CERT=true` (then any holder of the token can connect and a warning is logged). The bundle is re-read like the server certificate |
| `RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY` | A boolean (`true/false/1/0/yes/no`; anything else stops startup). When `true`, the plain listener serves `/internal/**` only to peers on the gateway's own host network (the engine on the same compose network) and answers `403 internal_tls_required` to anyone else |

The client side reads its certificate files once at first use: rotating the client certificate or the CA needs a restart of the Inference API (and of any other service that sets these keys). an operator file creates a private CA and both certificates; the firewall and bring-up steps are in an operator document "Gateway internal TLS link".

`Compute:Mode` is not involved: a local model works without `Compute:Mode=Fabric`, but the runner gateway must be deployed and reachable. With no token the call fails with the "not set up on this installation" reason. See [`compute-fabric.md`](compute-fabric.md).

## Runner-side controls

The desktop app (Local model screen; `runner/internal/config/localllm.go`, `runner/internal/llmrelay`):

- Sharing is opt-in and off by default. A `runner.json` written before the feature shares nothing.
- The base URL defaults to `http://127.0.0.1:11434`. Only a loopback host is accepted unless the user ticks the explicit "another computer on my network" option. The URL may not contain credentials, a query, a fragment or dot segments.
- Detection probes loopback ports 11434 (Ollama), 1234 (LM Studio), 8080 (llama.cpp) and 8000 (vLLM) with `GET /v1/models`.
- Concurrency cap: default 2, at most 8; beyond it the runner answers `busy`.
- Caps: request body 3 MiB, response 32 MiB (64 MiB for embeddings), chunks 256 KiB; default timeout 5 minutes, maximum 15; 5 second connect timeout; the runner waits for the response headers up to the request timeout, and running out of time is reported as `timeout`. An oversized request body is refused with `413 request_too_large` on every path. Redirects are refused.
- The model list is refreshed every five minutes (and on connect).
- Nothing about a prompt or answer is written to disk or to a log. The UI shows counters and the time of the last request.
- A paused runner (the existing pause control) answers `disabled` and serves nothing.

## Threat model

**What leaves the platform.** The prompts, and any images in a vision call, of every step that uses the provider are sent to a member's device. That device belongs to the same organisation, but the person who owns it can read everything it receives. The app shows a consent line when sharing is switched on.

**What the runner owner can see.** Every request body routed to their device and, because they control the model server, every answer they choose to return. The platform does not hide anything from the runner's owner.

**What the platform trusts.** Nothing the runner says. Capabilities (including the model list) are claims. Responses are data: status, content type and size are validated, an invalid start or chunk becomes `502 runner_rejected_request`, and total size is capped. A runner can only answer requests addressed to its own connection; a response for another runner's request id is dropped.

**SSRF controls.** The gateway cannot choose a host, URL, header or query: the request carries only a method, one of three allowlisted paths (chat, models, embeddings) and a JSON body. The runner builds the URL from its own settings, accepts loopback unless the owner opted in, refuses credentials and redirects, and never takes a URL from a message.

**Cross-organisation controls.** The organisation in the route comes from the provider row, never from a request. The gateway matches it against the device's own organisation and answers `404 device_not_found` on mismatch or revocation. The Inference API checks the same at write time. The model-list endpoint returns only the caller's organisation, only for Owner/Admin.

**Replay and abuse.** The internal routes need the shared service token, which never reaches a browser. Request ids are unique per connection and a duplicate is refused. The gateway bounds in-flight requests per runner and the runner bounds its own concurrency.

**Known limits.**

- Multi-replica relies on `RUNNER_GATEWAY_PEERS_HOST`. With it unset, a replica that does not hold the runner's socket answers `503 runner_offline`. With it set, that replica forwards to up to 8 peers (only to cluster-internal addresses: loopback, private or link-local, because the internal token travels with the call; one hop only, marked by the `X-ReelBolt-Relay-Hop` header, after the device and organisation are checked); if no peer holds the socket the answer is still `503 runner_offline`, and unreachable peers are reported as such.
- The remote model list is refreshed on the runner's side; the gateway sees changes only after a reconnect.
- The step-result cache fingerprint ignores which device served the call, so a cached result can be reused after the device or its model changes.
- A failure after streaming has started cannot change the HTTP status, so the gateway aborts the connection; the .NET client maps the dropped connection to `LocalRunnerUnavailableException`, so a half answer is a failed call, not a complete one.
- Model quality and speed are the owner's; the platform makes no promise about them.

## Operator checklist

1. Deploy the runner gateway and set `RUNNER_INTERNAL_TOKEN` to the same value on the gateway, the Inference API and the engine.
2. Set `Compute:GatewayBaseUrl` on the Inference API and the engine if the gateway is not at `http://runner-gateway:8090`.
3. Keep the internal routes off the public network. In a two-host deployment use the TLS link (`RUNNER_GATEWAY_INTERNAL_TLS_*`, `Compute:GatewayRequireTls=true`), bind it to the private address only and firewall it to the other host; set `RUNNER_GATEWAY_INTERNAL_PLAIN_LOCAL_ONLY=true` so the plain port refuses cross-host `/internal` calls.
4. With more than one gateway replica, set `RUNNER_GATEWAY_PEERS_HOST` to the DNS name of the headless service that resolves to every replica (empty means off). Without it, send the internal calls to the replica that holds the runner's socket.
5. Verify: with no bearer, a route answers 401; with no token configured, 503 `internal_api_disabled`; with a token and no runner, 503 `runner_offline`.
6. Do not log request or response bodies at any proxy in the path.
