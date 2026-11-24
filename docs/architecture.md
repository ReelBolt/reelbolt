# Architecture overview

ReelBolt is a set of containerized services behind one nginx reverse proxy. This page is the map; each
area links to its detailed document.

```
                         ┌──────────────┐
                         │    Nginx     │ :80 / :443
                         └──────┬───────┘
        ┌───────────┬───────────┼───────────┬────────────┐
        ▼           ▼           ▼           ▼            ▼
   ┌────────┐ ┌──────────┐ ┌────────┐ ┌─────────┐ ┌──────────┐
   │  Site  │ │   Docs   │ │  Web   │ │  Go API │ │Inference │
   │   /    │ │  /docs/  │ │  /app  │ │ (auth)  │ │   API    │
   └────────┘ └──────────┘ └────────┘ └─────────┘ └────┬─────┘
                                                       │ RabbitMQ
                                                       ▼
                                                ┌─────────────┐
                                                │  Workflow   │──► Sandbox (Remotion)
                                                │   Engine    │──► ffmpeg (in-container)
                                                └─────────────┘
   Shared: PostgreSQL · Garage (S3) · Qdrant (vectors) · inference providers (LLM, ASR, vision, TTS, video)
```

## Services

| Service | Role | Details |
|---------|------|---------|
| `nginx` | Single entry point, TLS, cookie → `Authorization` header | [tls.md](tls.md) |
| `site` | Public marketing site at `/` | [marketing-site.md](marketing-site.md) |
| `docs` | This documentation, built with Docusaurus, at `/docs/` | [docs-site.md](docs-site.md) |
| `web` | Authenticated dashboard at `/app` (Next.js + Mantine) | — |
| `go-api` | Users, login, JWT issuance, organizations (workspaces, members, invites), workflow stats + SSE relay | [multi-tenancy.md](multi-tenancy.md) |
| `inference` | Projects, files, agents, workflows CRUD; the platform assistant; file + docs vector search; the credit ledger, plans and the billing webhook | [platform-assistant-mcp.md](platform-assistant-mcp.md), [platform-docs-search.md](platform-docs-search.md), [metering-and-billing.md](metering-and-billing.md) |
| `workflow-engine` | Runs workflows: agents, rooms, video analyze/compile, generation, voiceover | [workflow-execution-optimization.md](workflow-execution-optimization.md), [video-editing.md](video-editing.md) |
| `sandbox-executor` | Isolated containers that build and render Remotion projects for agents | [sandbox-service.md](sandbox-service.md) |
| `runner-gateway` | WebSocket endpoint runners dial; the compute fabric's job queue and routing (opt-in, `fabric` compose profile) | [runner-protocol.md](runner-protocol.md), [compute-fabric.md](compute-fabric.md) |
| `qdrant` | Vector store for project files and platform docs | [embeddings.md](embeddings.md) |

## Tenancy: every request is scoped to one organization

ReelBolt's cloud product serves several **organizations** (workspaces) from one deployment, and the
boundary between them is a property of every layer rather than a check bolted onto the API:

- **The token names one active organization.** The Go API issues the JWT and puts the caller's
  active org in it as `org`, with `orgRole` and `platformAdmin` alongside. None of those three is
  trusted: both services re-read the caller's membership from the database before serving the
  request, so a revoked membership, a demotion or a promotion applies within about a minute.
- **The database enforces the boundary too.** The Inference API's `InferenceApiDbContext` carries a
  named EF Core query filter that scopes the tables a caller can address by an id of their own
  choosing, and it **fails closed** when there is neither a request org nor a system scope. A
  background job must open a system scope deliberately.
- **A foreign resource is `404`, never `403`.** Existence never leaks: a project, thread or
  execution in another organization gives exactly the answer a nonexistent one gives. A resource
  inside the caller's own organization that their role may not touch is the only `403`.
- **Platform administration is separate from organization authority.** The Go API's
  `/api/v1/admin/*` routes and the platform pages are gated on `platformAdmin`, which is backed by
  `application_users.is_admin` and is never granted by a workspace role. Everything under
  `/api/v1/orgs/*` (the Go API) and `/api/v1/org/*` (the Inference API — note the singular, so the
  two never collide) is gated on the caller's role in that specific organization.

The full model — kinds, roles and the permission matrix, the claims, project access, the query
filters and the platform-managed vs bring-your-own inference providers — is
[multi-tenancy.md](multi-tenancy.md). What an organization spends is [metering-and-billing.md](metering-and-billing.md).

## How a workflow runs

1. The dashboard (or the assistant) asks the Inference API to execute a workflow. It writes a `Queued`
   execution row and publishes `WorkflowExecutionRequested` to RabbitMQ.
2. The Workflow Engine acknowledges the message immediately and runs the execution on a background task,
   step by step, using a step executor per step type (agents, rooms, deterministic video steps…).
3. Each completed step is persisted and published; the Go API relays progress to the browser over SSE
   ([realtime-workflow-execution.md](realtime-workflow-execution.md)).
4. Renders and artifacts land in object storage; the dashboard lists them as outputs.

## Where a step runs: the compute fabric

Every step runs **in the engine process, on the engine's host** — the `sandbox-executor` for
model-authored Remotion code and in-process ffmpeg — unless an operator turns the **compute fabric**
on. The fabric can place a step on the customer's own paired desktop machine or on a worker VM in our
cloud pool instead; its control plane is the Go `runner-gateway` and its data plane is the runner
protocol ([runner-protocol.md](runner-protocol.md)).

`Compute:Mode` defaults to `Local` and is deliberately absent from the shipped
`appsettings.json`, so `docker compose up` is unchanged and the fabric is inert for every
self-hosted install. The selector's decision table, the deployment hazards and — importantly —
**which parts of the fabric are not wired yet** are in [compute-fabric.md](compute-fabric.md).

## Polled reads: conditional GET and the summary run view

Polled JSON reads (a run, the assistant activity and thread lists, the agents list, the dashboard overview, the billing summary,
a project's outputs) carry the `[ConditionalGet]` result filter. It serializes the body once, sends a weak `ETag` derived from the
body bytes only, `Cache-Control: private, no-cache` and `Vary: Cookie, Authorization`, and answers a matching `If-None-Match`
with `304` and no body, so a browser revalidates on its own and an unchanged body is never downloaded again.

`GET /api/v1/projects/{projectId}/executions/{executionId}?detail=summary` returns each step result without its heavy fields
(`output` empty; `inputJson`, `toolCallsJson`, `reasoningJson`, `chatTranscriptJson` and `outputJson` null, except that an
`outputJson` carrying `warnings` keeps just `{"warnings":[...]}`) and marks it `detailOmitted: true`. Without the parameter, or with
any other value, the full payload is unchanged. One full result is
`GET /api/v1/projects/{projectId}/executions/{executionId}/step-results/{resultId}`.

## Where the details live

- Built-in agents and their tools: [builtin-agents.md](builtin-agents.md)
- Inference providers: [anthropic-provider.md](anthropic-provider.md), [gemini-provider.md](gemini-provider.md),
  [deepseek-provider.md](deepseek-provider.md), [embeddings.md](embeddings.md)
- Video: [video-editing.md](video-editing.md), [video-generation.md](video-generation.md), [voiceover.md](voiceover.md),
  [editor.md](editor.md)
- Decision models and guardrails: [decision-models.md](decision-models.md)
- Tenancy — organizations, roles and the tenant boundary: [multi-tenancy.md](multi-tenancy.md)
- Usage, credits, plans and the billing provider boundary: [metering-and-billing.md](metering-and-billing.md)
- CI: [ci.md](ci.md)

The repository-level `CLAUDE.md` holds the exhaustive reference (every config key, table ownership,
migration rules).
