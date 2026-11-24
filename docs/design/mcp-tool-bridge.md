# MCP tool bridge — frozen interface

**Owner:** Lead (this document is the contract; do not change it unilaterally — raise it with the Lead).
**Status:** frozen for the build in flight.

## 1. Decision

There is exactly **one implementation of each assistant tool**, and it is the existing C# one
(`AssistantToolProvider`). The MCP server becomes a **thin proxy** over a new pair of Inference API
endpoints. No tool logic is written in TypeScript.

## 2. Current state (evidence)

| Fact | Evidence |
|---|---|
| The assistant builds its tools in-process and never calls the MCP server | `inference/src/ReelBolt.Inference.Api/Controllers/AssistantChatController.cs:320,325`, `Agents/Tools/AssistantToolProvider.cs:150` |
| Tool count at this decision | **118** chat tools and a **79**-tool MCP projection (§3); the per-group breakdown is in `CLAUDE.md` and [`platform-assistant-mcp.md`](../platform-assistant-mcp.md) |
| MCP server today | `mcp/src/server.ts` — hand-rolled JSON-RPC, only `tools/list` + `tools/call`, **no `initialize`**, 7 tools re-implemented in TS (`mcp/src/tools/*.ts`) |
| MCP dependencies today | `mcp/package.json` — only `jose`; no MCP SDK |

**Answer to "does this double the assistant's tools?"** Today: no doubling of the *assistant* (it is
fully in-process), but the MCP server's 7 tools **do** duplicate API behaviour in TS. After this
change there is no duplicate implementation anywhere: the MCP lists what the API has and forwards
the call.

## 3. Frozen HTTP contract (Inference API)

Both endpoints authenticate with the **existing** `middleware.Auth` JWT. Authority for every gated
tool is derived server-side, per call, inside the tool body — exactly as the chat path does today.
Nothing about authority may be passed in from the MCP client.

### `GET /api/v1/assistant/tool-catalog`

- No parameters, no page context required.
- 200 → `{ "tools": [ { "name": string, "description": string, "inputSchema": <JSON Schema>, "title": string, "annotations": { "title": string, "readOnlyHint": bool, "destructiveHint": bool, "idempotentHint": bool, "openWorldHint": bool } } ] }`
- The set is the **MCP projection** of `AssistantToolProvider.GetTools(...)`, not the raw registration:
  `AssistantMcpToolSurface.Project` publishes **79** tools. `WatchExecution` is chat-only; the 39
  `Propose*` / `Apply*` pairs collapse to 38 single tools (the MCP call runs both halves in one
  request and never exposes the confirmation token), and the upload pair stays two calls
  (`BeginUpload` read-only, `CompleteUpload` destructive). `ExecuteWorkflow` and every read tool keep
  their names. The old `Propose*` / `Apply*` names are not listed and are refused as unknown.
- Every entry carries a `title` (also inside `annotations`) and the four MCP hints. Every tool is
  exactly read-only or destructive; the catalog test rejects anything else.
- Admin-gated tools are listed only when the caller's `mcp_admin_tools_enabled` opt-in is on;
  otherwise they are omitted from the catalog, and calling one by name is refused with the same
  admin-tools result. With the opt-in on, the gate is also enforced at call time inside the tool body
  (the live `ICurrentUser`), never by the catalog.
- `inputSchema` is read from the `AIFunction` itself. **Never hand-written, never maintained in a
  second place.** The one change to a declared schema is the typed run `brief` object
  (`AssistantToolSchemas.RunBriefSchema`, matching `RunBriefValidator`; all fields optional, unknown
  fields refused), and the collapsed pair's schema is the Propose half's minus `confirmationToken`.

### `POST /api/v1/assistant/tool-call`

```jsonc
{
  "name": "ExecuteWorkflow",          // required, must be a name from the catalog
  "arguments": { /* the tool's own arguments, verbatim */ },
  "context": {                        // optional page context
    "projectId": "guid?",
    "workflowId": "guid?",
    "imageDocumentId": "guid?"
  }
}
```

- 200 → `{ "content": [ { "type": "text", "text": "<the tool's result, as a string>" } ], "isError": false }`
- **Tool-level refusals are results, not errors**: an admin gate refusal, a plan-gate refusal, a
  rate-limit refusal and an unknown workflow all come back as the tool's own JSON text with
  `isError: false`, because the model is meant to read them. `isError: true` is reserved for
  transport/execution failure.
- Unknown or unregistered `name` → **404**, body carries the tool name. Never a 403.
- These calls are **not chat turns**: the endpoint must not create, require or touch an assistant
  thread, and must not append to chat history.
- Page context reaches the provider as
  `new AssistantToolContext(userId, projectId, workflowId, imageDocumentId)`
  (`Agents/Tools/AssistantToolContext.cs:42`), with `userId` from the verified JWT `sub` — never
  from the request body.

## 4. How page context crosses MCP

The assistant's project-scoped tools take no `projectId` argument; they read it from the per-call
context. Over MCP the caller must therefore supply it. The MCP server adds **one** optional property
to every bridged tool's `inputSchema`:

```jsonc
"context": {
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "projectId":       { "type": "string", "format": "uuid" },
    "workflowId":      { "type": "string", "format": "uuid" },
    "imageDocumentId": { "type": "string", "format": "uuid" }
  }
}
```

The MCP server strips `context` out of the arguments and sends it as the `context` field of
`tool-call`; everything else is forwarded untouched. A single wrapper property (rather than three
top-level ones) is used precisely because real tools already declare `workflowId` — see
`ProposeWorkflow`/`ApplyWorkflow`/`ExecuteWorkflow` in the chat, and `SaveWorkflow` / `ExecuteWorkflow`
over MCP. **A guard test must assert that no bridged tool
declares a property named `context`.**

### Known caveat: a nullable parameter with no default is required

`AIFunctionFactory` treats a nullable parameter that declares no default value as **required** in the
arguments dictionary. `GetProject(Guid? projectId)` therefore throws *"The arguments dictionary is
missing a value for the required parameter 'projectId'"* when the key is absent — even though its own
description says the project may be omitted in favour of the ambient context. Passing an explicit
`null` works and falls back to the context as documented.

This is **pre-existing assistant behaviour** — the chat path has exactly the same property — so the
bridge deliberately does not paper over it: the catalog publishes the schemas the assistant already
has, those parameters are marked required in them, and an MCP client that reads the schema will supply
them. **Do not add argument defaulting in the MCP layer**; that would be a second, silent semantics
diverging from chat. Fixing the underlying binder behaviour, if it is to be fixed at all, belongs to
the assistant, not to the bridge.

## 5. MCP server requirements

- Proper MCP protocol: `initialize`, `notifications/initialized`, `tools/list`, `tools/call`,
  `ping`, and correct JSON-RPC error codes. `tools/list` results carry `nextCursor: null` absent.
  Each listed tool forwards its `title` and `annotations` (`readOnlyHint`, `destructiveHint`,
  `idempotentHint`, `openWorldHint`) from the catalog unchanged; the catalog decides them.
- Prefer the official `@modelcontextprotocol/sdk`. **Verify the install works first**; if npm is
  unavailable or the install fails, hand-roll a protocol-complete server instead and say so — do not
  silently ship a server that fails `initialize`, because Claude Code will not connect to it.
- Transports: stdio (token from `REELBOLT_MCP_TOKEN`) and streamable HTTP (token from the
  `Authorization` header). Read once at the transport boundary — **never** a tool argument.
- The token is **forwarded, not verified** by the MCP server — see section 9. `JWT_SIGNING_KEY`
  is **not required**.
- Failure of the API (unreachable, non-200) must surface as a JSON-RPC error, not a crash.
- `mcp/package.json` `test` script must actually run the tests (today it is `node --test
  dist/**/*.test.js`, which depends on glob expansion).

## 6. Claude Code plugin requirements

- Location: `plugins/reelbolt/`, structured so it can be dropped into a marketplace unchanged:
  `.claude-plugin/plugin.json`, the MCP server registration, and at least one command or skill.
- Document install (`claude plugin install …`) and the required env (`REELBOLT_MCP_TOKEN`,
  `INFERENCE_API_URL`).
- **Never ship a default or example signing key.** The server already fails closed on a missing
  `JWT_SIGNING_KEY`; keep that.

## 7. Non-goals

- No user-management or workflow-engine tools (the Inference→Go hop is closed).
- No change to the assistant's own tool set or its authority rules.
- `mcp/**` is not proxied by nginx **in stdio mode**, which stays a local CLI/programmatic surface.
  (Superseded for the HTTP transport by [`mcp-oauth.md`](mcp-oauth.md) §8, which publishes `/mcp` and
  the two discovery documents so a client can authenticate with OAuth.)

## 8. Verification gate

Local evidence is the merge gate (CI is down by ruling). Required before acceptance:
`npm test` + `tsc --noEmit` under `mcp/`, `dotnet build` + `dotnet test` for the touched .NET
projects, and an end-to-end proof that `tools/list` returns the full catalog and that a real
`tools/call` round-trips through the API.

## 9. Authentication: forward, never verify (supersedes any earlier note)

The MCP server **must not verify the JWT itself**, and **must not require `JWT_SIGNING_KEY`**.

Why. The Inference API is already a complete authenticator: `AddJwtBearer` with a symmetric
`IssuerSigningKey` plus [`MembershipValidator`](../../inference/src/ReelBolt.Inference.Api/Services/Auth/MembershipValidator.cs)
on `OnTokenValidated` (`InferenceApiServiceCollectionExtensions.cs:59-74`). It re-derives authority
per request from the database, exactly as section 3 requires. The MCP verifying the same token with
the same HMAC key adds nothing except a fail-fast, and it costs something severe:

`docker-compose.yml:420` currently feeds the platform's **token-signing secret** into the MCP
container. For a self-host operator that is merely redundant. For the shipped Claude Code plugin it
is a defect — a plugin installed on a user's machine would need the platform's signing key to
authenticate them, and anyone holding that key can mint a valid token for **any** account. A
user-installed CLI must never be given the issuer's secret.

So:

- The MCP server takes the bearer token at the transport boundary and passes it through as
  `Authorization: Bearer <token>` on every call to the Inference API. It never parses, decodes or
  validates it.
- `401`/`403` from the API is surfaced as a JSON-RPC error. The API is the only authority.
- `JWT_ISSUER` / `JWT_AUDIENCE` are consequently not needed by the MCP server either — it has no
  opinion about token contents.
- Optional local pre-validation is permitted **only** when `JWT_SIGNING_KEY` happens to be set
  (self-host), as a courtesy fail-fast. It must never become a requirement, and must never be
  documented as one.
- The `jose` dependency is therefore no longer needed for authentication.
- The plugin's documented env is: the user's ReelBolt JWT (`REELBOLT_MCP_TOKEN`) and
  `INFERENCE_API_URL`. Nothing else is required.

### Deployment follow-ups

1. **Done by the Lead: nginx bearer pass-through.** `nginx/auth.js`'s `getAuthHeader` returned an
   empty string whenever the request had no `reelbolt_token` cookie. Every `/api/v1/` location does
   `proxy_set_header Authorization $auth_header`, so an empty value *overwrites* the client's own
   header — a cookie-less CLI, which is exactly what this MCP server is, had its valid bearer token
   blanked and was 401'd. `getAuthHeader` now falls back to the incoming `Authorization` header. This
   grants nothing by itself: the Inference API validates signature, issuer and audience itself. The
   cookie remains the primary path for browsers. Verified: `node --check` on the ESM file passes.
2. **Open:** `docker-compose.yml` should drop `JWT_SIGNING_KEY`, `JWT_ISSUER` and `JWT_AUDIENCE` from
   the `mcp` service once the server no longer reads them (team task `task-6`).
3. **Reachability, for the plugin's docs.** The `inference` service publishes no host port
   (`docker-compose.yml:308`); only nginx is published (`:878-880`, `${APP_PORT:-80}:80`). A local
   stack is therefore reached at `http://localhost`, and the bridge endpoints arrive as
   `http://localhost/api/v1/assistant/tool-catalog` and `.../tool-call` through nginx's generic
   `/api/v1/` locations. `INFERENCE_PORT=3001` in `.env.example` is vestigial — nothing binds it.
