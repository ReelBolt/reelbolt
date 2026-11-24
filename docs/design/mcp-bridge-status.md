# MCP tool bridge — status and verification

**Last updated:** 2026-10-11, after independent verification.
**Contract:** [`mcp-tool-bridge.md`](mcp-tool-bridge.md) · **Tool audit:** [`mcp-tool-inventory.md`](mcp-tool-inventory.md)

## Goal

Expose **all 59** of the platform assistant's tools over MCP, with a single implementation of each
tool (no duplication), and ship an official Claude Code plugin that consumes it.

## The "does this double the assistant's tools?" answer

**No — and it never did on the assistant side.** The assistant builds its tools in-process in C# and
never calls the MCP server (`AssistantChatController` has no HTTP client and no reference to `mcp`;
`ExecutionInitiator.Mcp` is defined and unused). The duplication that *did* exist ran the other way:
`mcp/**`'s 7 hand-written TypeScript tools re-implemented API behaviour. The proxy rewrite deletes
them, so every tool now has exactly one implementation — in C#.

## Final state

| Work | Evidence |
|---|---|
| C# bridge (`GET /api/v1/assistant/tool-catalog`, `POST /api/v1/assistant/tool-call`) | 14 bridge tests pass; full suite **7443 passed / 0 failed / 15 skipped**; `dotnet format --verify-no-changes` clean on both touched projects |
| MCP server as a thin proxy | `npx tsc --noEmit` → 0 errors; `npm test` → **23 passed / 0 failed** |
| Tool parity | Independent verification diffed the **live** catalog against names re-derived from the C# source: byte-identical, 59 names, 13 gated |
| Claude Code plugin (`plugins/reelbolt/`) | `claude plugin validate --strict` passes on the marketplace, the plugin manifest *and* the skills; loads via `--plugin-dir`; tree is secret-free |
| nginx bearer pass-through | `getAuthHeader` no longer blanks a cookie-less client's own `Authorization` header |
| compose cleanup | JWT vars removed from the `mcp` service; `docker compose config --quiet` → exit 0 |
| End-to-end (Lead, after fixes) | Real MCP server over HTTP against a stub API: 2 independent sessions initialize, `tools/call` round-trips, `Content-Type: application/json` observed at the API, token forwarded verbatim |

## What independent verification caught, and what was done

Verification stood up a **real Inference API** (against a clone of a developer Postgres volume) and
drove it from the real MCP server. It found a blocker the author's stub-based tests structurally
could not:

1. **BLOCKER — `mcp/src/apiClient.ts` never set `Content-Type`.** `fetch` with a string body
   defaults to `text/plain`, and the API's `[ApiController]` + `[FromBody]` answered
   **415 Unsupported Media Type**. So `tools/list` (GET) worked and **every `tools/call` failed**.
   *Fixed:* the header is now set when a body is present, with a regression test asserting it, and
   the fix was proven end-to-end.
2. **MAJOR — the HTTP transport allowed only one session per process.** A single stateful transport
   was created at startup, so the second `initialize` got
   `400 {"code":-32600,"message":"Invalid Request: Server already initialized"}` for the container's
   lifetime. *Fixed:* one Server + transport **per session**, keyed by `mcp-session-id`, disposed on
   close; proven by two concurrent sessions initializing in the end-to-end run.
3. **MINOR — the MCP test fixture was tautological.** It padded itself to 59 with synthetic names
   (`Tool07`, `PadTool00`, …), so the "59 tools" assertion could not fail, and 33 of its names did
   not exist. *Fixed:* the fixture now carries the **real 59 names** (recorded from the C#
   registrations) with a self-check that it is 59 unique names.
4. **MINOR — plugin docs described behaviour the server does not have** (an optional
   `JWT_SIGNING_KEY` fail-fast, and an `INFERENCE_API_URL` fallback). *Fixed:* both reworded.
5. **MINOR — a skill said a fabricated `confirmationToken` is rejected with `409`.** It is actually
   HTTP 200 with `isError:false` and the tool's own "stale confirmationToken" text. *Fixed.*
6. **NIT — this file claimed `mcp/.gitignore` still ignored the lockfile.** *Fixed*, and the ignore
   line was removed (see below).

## Deliberately changed outside the two workstreams

- `mcp/.gitignore` — `package-lock.json` was ignored while `Dockerfile` runs `npm ci`, which
  *requires* a committed lockfile. The file was tracked so builds worked, but the ignore line was a
  trap; it is gone. `npm ci --dry-run` is clean.

## Known issues NOT fixed here (out of scope, but they gate "production-ready")

1. **A fresh database cannot migrate at all.** On an empty volume the API's migration
   `20261004120000_AddDailiesRoomConfigJson` does `AddColumn` on engine-owned `workflow_steps` before
   that table exists (`42P01`), and running the engine first fails on `projects.organization_id`
   (`42703`). `docker compose up` on a clean volume therefore deadlocks. This is a **pre-existing
   dailies-feature defect**, not a bridge defect — the one observed fix is to fold the API's
   duplicate column migration into the engine migration that owns the column.
2. **`AIFunctionFactory` treats a nullable parameter with no default as required.** Omitting e.g.
   `GetProject`'s `projectId` throws rather than falling back to the ambient context; passing
   `null` works. Pre-existing assistant behaviour, deliberately not papered over (contract §4).

## What is NOT proven

- **No call has been made behind nginx.** The nginx bearer pass-through is reasoned from
  `locations.conf` + `node --check`; the end-to-end runs called the API directly. One cookie-less
  request through the front door would close this.
- **The plugin was never installed into a real Claude Code session.** Manifest validation and
  `--plugin-dir` loading pass; tool namespacing
  (`mcp__plugin_reelbolt_reelbolt__<ToolName>`) and `${user_config}` substitution are
  documentation-verified only.
- **The MCP↔API end-to-end used a stub API**, not the real Inference API. The verifier drove the real
  API against the *bridge* and the Lead drove the *proxy* against a stub; the two were not joined in
  one run because bringing up the real stack needs a database workaround for issue 1 above.
- **Most mutating tools were never executed.** Only `ApplyWorkflow`'s fabricated-token refusal was
  exercised live; the other 19 are covered by their C# unit tests.
- **The assistant chat path was never executed** (no LLM called). Its construction site and static
  properties were verified, and the shared-factory refactor was diffed as behaviour-preserving.

## How to run

```bash
# MCP server
cd mcp && npm ci && npm run build && npm test && npx tsc --noEmit

# C# bridge
dotnet test inference/tests/ReelBolt.WorkflowEngine.Tests/ReelBolt.WorkflowEngine.Tests.csproj \
  --filter "FullyQualifiedName~AssistantToolBridge"

# Plugin
claude plugin validate ./plugins/reelbolt --strict
claude plugin validate ./plugins/reelbolt/.claude-plugin/plugin.json --strict
```

Sandbox note: the npm cache is read-only here — add `--cache /tmp/npmcache` if an npm command fails
with `EROFS`. Never bake that flag into committed files.
