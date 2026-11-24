# MCP OAuth2 — frozen interface

**Owner:** Lead. This is the contract for the OAuth + metering work; do not change it unilaterally.
**Companion:** [`mcp-tool-bridge.md`](mcp-tool-bridge.md) (the tool bridge this builds on).

## 1. Decisions (confirmed with the operator)

| Decision | Choice |
|---|---|
| Hosting | The MCP server is **hosted remotely over HTTPS** and reachable through nginx, so Claude Code can run an OAuth login. The **local stdio mode stays** for self-host/advanced users. |
| Charging | **Meter every MCP call and attribute it to the user; charge credits only where the underlying work already costs money** (running a workflow, paid AI picture tools, vision lookups). Free reads stay free. |
| Token scope | The MCP token is **narrower than a dashboard session**: assistant tools only, no admin surfaces. Enforced by the services, see section 3a. |
| Admin opt-in | A **per-user setting in the web portal** (`/app/settings`) enables admin tools over MCP. Off by default. |

**This overturns a previously documented decision** ("the MCP server is NOT proxied by nginx",
`docs/platform-assistant-mcp.md`). That section was rewritten as "MCP exposure through nginx", not
deleted: **stdio** mode still keeps the original rationale, and only the HTTP transport is published.

## 2. Why this is buildable on what exists

- The Go API is already the sole issuer of HS256 ReelBolt JWTs
  (`api/services/jwt_service.go`: `Issuer = Cfg.JWTIssuer`, `Audience = Cfg.JWTAudience`, signed with
  `Cfg.JWTSigningKey`).
- The Inference API validates exactly those (`InferenceApiServiceCollectionExtensions.cs:59-74`:
  ValidateIssuer / ValidateAudience / IssuerSigningKey / ValidateLifetime) and then re-reads
  membership from the database (`MembershipValidator`).
- So an **OAuth access token IS a normal ReelBolt JWT plus a `scope` claim.** No new IdP, no second
  permission model, no change to how the Inference API authenticates — the same "no new IdP" rule the
  assistant already follows (`docs/platform-assistant-mcp.md` §Authentication).
- An **unused `ExecutionInitiator.Mcp`** already exists, which is how MCP-caused work is labelled.
- `UsageSource` is persisted by member name, so appending a member needs no migration.

## 3. Scope strings

| Scope | Meaning | Granted when |
|---|---|---|
| `assistant.tools` | Use the MCP tool set (all non-gated tools). | Always, at consent. |
| `assistant.admin` | The 13 gated tools are also visible and callable. | Only if `mcp_admin_tools_enabled` is true for that user **at consent time**. |
| `runs.watch` | Read the status of runs, nothing else: one tool, `GetExecutionStatus`. Used by the plugin's `reelbolt-watch` command. | Only when it is requested on its own (no `assistant.*` scope in the request); then exactly `runs.watch` is granted. Alongside an assistant scope it is dropped, so it never widens another token. |

Anything else is not a scope this deployment understands and must be dropped at consent, never echoed
back as granted.

## 3a. Enforcement: a scoped token is not a session

The `scope` claim is **enforced**, deny by default (fix for issue #565; before it, nothing read the
claim and an OAuth token worked on every REST route as the user, with no approval cards). The rule is
the same in every service that validates the JWT: **a token carrying any `scope` claim, whatever its
value, is refused (`403 {"error":"mcp_token_scope"}`) unless the request matches an explicit allowlist
entry for which the token holds a required scope.** Presence of the claim marks the token; an empty or
unknown value gets no extra rights. (The Go API, which decodes the claim into a string, refuses a
non-empty value; it never mints an empty one: `GenerateOAuthAccessToken` returns an error for an empty
scope.)

| Service | Allowed for a scoped token | Everything else |
|---|---|---|
| Go API (`middleware.Auth`, every authenticated route; the public `/oauth/token`, `/oauth/revoke`, `/oauth/register` and metadata routes take no bearer and are unchanged) | nothing | `403` |
| Inference API (`McpScopeGuard`) | `GET /api/v1/assistant/tool-catalog`, `POST /api/v1/assistant/tool-call` (`assistant.tools` or `assistant.admin`); `POST /api/v1/assistant/tool-call` also for `runs.watch`, where `AssistantToolBridgeController` then refuses (`403 mcp_token_scope`) every tool except `GetExecutionStatus`, before any lookup or charge | `403` |
| Workflow Engine (`McpScopeGuard`) | `GET /api/v1/workflow-engine/decision-calibration` (`assistant.admin` only; the gated calibration tool forwards the caller's bearer there) | `403` |

Why the Go API allows nothing: it hosts `POST /api/v1/auth/switch-org` (mints a fresh, unscoped
session, which would launder the token into a full one), `PATCH /api/v1/auth/settings` (would let an
MCP token switch `mcp_admin_tools_enabled` on for itself) and `/oauth/authorize` (consent must be given
by a human browser session, never by an MCP token). The MCP server itself only ever calls the two
Inference API routes above.

What is deliberately **not** affected: dashboard sessions and every token without a `scope` claim; the
engine's 5-minute on-behalf-of tokens (`act=workflow-engine`, no `scope`); runner device tokens
(`kind=runner`) and the Ed25519-signed `/api/v1/device/**` surface. nginx does not enforce scope (it
cannot read claims; its `getAuthHeader` passes a client `Authorization` header through unchanged when there is no `reelbolt_token` cookie), so
the services are the gate. Existing access tokens are covered at once because they already carry
`scope`; no client has to re-authorize.

### Stdio `REELBOLT_MCP_TOKEN`

Stdio mode is **not scoped**. The value is the user's own ordinary ReelBolt session JWT (no `scope`
claim), pasted by the user into a local process they control; whoever holds it already holds a full
session and could call the REST API directly, so scoping the MCP server's use of it would protect
nothing. The scoped, least-privilege path is the hosted OAuth flow; use it for any client that should
not hold a session. A scoped stdio token (a personal access token with its own issuance and revocation)
would be a separate piece of work.

## 4. Go API — OAuth 2.1 authorization server

Paths are under `/oauth` deliberately: `/api/v1/auth/oauth/{provider}` is the existing **client**
(Google/GitHub sign-in) surface and must not be disturbed.

### `GET /.well-known/oauth-authorization-server` (RFC 8414)

```json
{
  "issuer": "<public origin>",
  "authorization_endpoint": "<origin>/oauth/authorize",
  "token_endpoint": "<origin>/oauth/token",
  "registration_endpoint": "<origin>/oauth/register",
  "revocation_endpoint": "<origin>/oauth/revoke",
  "scopes_supported": ["assistant.tools", "assistant.admin"],
  "response_types_supported": ["code"],
  "grant_types_supported": ["authorization_code", "refresh_token"],
  "code_challenge_methods_supported": ["S256"],
  "token_endpoint_auth_methods_supported": ["none", "client_secret_post"]
}
```

### `POST /oauth/register` (RFC 7591, dynamic client registration)

Public clients (`token_endpoint_auth_method: "none"`, PKCE only) are the primary case; a client secret
may be issued for confidential clients. Returns `client_id`, `client_name`, `redirect_uris`,
`grant_types`, `response_types`, `token_endpoint_auth_method`, `client_id_issued_at`.
**Redirect URIs are matched exactly — never by prefix or wildcard.** A registration with no redirect
URI is refused.

### `GET /oauth/authorize`

Requires an authenticated browser session. If absent → redirect to the app's sign-in with a return
path, **never** an API 401. Then a **consent screen** listing the scopes, the client's name, and
whether admin access is being granted. Denying returns `error=access_denied` to the client.
`code_challenge` + `code_challenge_method=S256` are **required**; anything else is `invalid_request`.

### `POST /oauth/token`

- `authorization_code`: verify PKCE (`S256`), single-use code, exact `redirect_uri` match, client match.
- `refresh_token`: rotate on use; a reused refresh token revokes the whole grant.
- Errors are RFC 6749 shaped (`invalid_grant`, `invalid_client`, `unsupported_grant_type`) **and never
  reveal whether a client id or code exists**.
- The access token is a ReelBolt JWT (section 2) with the granted scopes plus a `scope` claim;
  `expires_in` 3600, refresh lifetime 30 days.

### `POST /oauth/revoke` (RFC 7009)

Revokes a refresh token for the presented client. Always answers 200, whether or not the token
existed (never an oracle).

## 5. Storage — and who creates it

**The Go API never migrates.** The Inference API owns the schema via EF Core migrations, so the
migration ships there and the Go API reads/writes by GORM/raw SQL, exactly as it already does for
`organizations` and `organization_memberships`.

New tables (Inference API migration `AddMcpOAuth`):

- `oauth_clients` — `id`, `client_id` (unique), `client_secret_hash` (nullable), `client_name`,
  `redirect_uris` (jsonb array), `grant_types`, `response_types`, `token_endpoint_auth_method`,
  `created_at`, `last_used_at`
- `oauth_authorization_codes` — `id`, `code_hash` (unique), `client_id`, `user_id`,
  `organization_id`, `redirect_uri`, `code_challenge`, `scope`, `expires_at`, `consumed_at`
- `oauth_refresh_tokens` — `id`, `token_hash` (unique), `client_id`, `user_id`, `organization_id`,
  `scope`, `expires_at`, `revoked_at`, `rotated_from_id`

Codes and refresh tokens are stored **hashed**, never in the clear.

New column (same migration): `application_users.mcp_admin_tools_enabled boolean NOT NULL DEFAULT false`.

## 6. Inference API — gating and metering

### Gating (the single rule)

`AssistantToolBridgeController` refuses a **gated** tool (`GetAdminToolNames`) unless the calling
user's `mcp_admin_tools_enabled` is true. One rule, applied identically to local and remote callers —
the setting is the authority, and the OAuth scope mirrors it for auditability rather than forming a
second, independently-failing gate.

- The **catalog omits gated tools** when the setting is off, so a narrow token is not even told the
  admin tools exist. Parity with the assistant is preserved on the *chat* path (which is unaffected);
  the MCP surface is deliberately narrower.
- With the setting on, gated tools are listed and still pass their own per-call authority check
  (`ICurrentUser.IsPlatformAdmin` / `OrgAuthority.IsOrganizationAdmin`) exactly as before. The
  setting is an additional condition, never a substitute.

### Metering

- New table `mcp_tool_calls` (same migration): `id`, `user_id`, `organization_id`, `tool_name`,
  `called_at`, `succeeded`, `error_code` (nullable), `duration_ms`.
  One row per bridged call, written whether it succeeded or was refused, so usage is reportable per
  user and abuse is visible. **A read tool's row is a record, not a charge.**
- MCP-caused workflow executions are labelled `ExecutionInitiator.Mcp` (the enum member already
  exists, unused) instead of `Assistant`.
- **`usage_events` is not touched.** It records what the platform *buys from a provider*
  (`docs/metering-and-billing.md`); a tool call is not a provider purchase, and writing zero-cost rows
  there would distort provider-cost reporting. Charging therefore stays exactly as it is today: the
  existing credit preflight and ledger already charge workflows and paid picture tools correctly, and
  the MCP path inherits them.

## 7. MCP server — OAuth resource server

- `GET /.well-known/oauth-protected-resource` (RFC 9728):

```json
{
  "resource": "<public MCP URL>",
  "authorization_servers": ["<public origin of the Go API>"],
  "scopes_supported": ["assistant.tools", "assistant.admin"],
  "bearer_methods_supported": ["header"]
}
```

- **Amends `mcp-tool-bridge.md` §5.** When the HTTP transport is asked to do anything without a
  bearer token, or when the Inference API rejects the presented token with 401, the server answers
  **HTTP 401 with `WWW-Authenticate: Bearer resource_metadata="<...>", scope="assistant.tools"`** so
  the client can discover the authorization server and start a login. Every other non-2xx from the API
  keeps today's behaviour (a JSON-RPC error).
- Endpoint paths are exactly `/.well-known/oauth-protected-resource` (metadata) and `/mcp` (the
  transport). nginx forwards both with the request URI preserved, so the server sees `/mcp` and must
  not reject a request for it — the streamable-HTTP transport already accepts any path.
- Configuration, both from the environment and never hardcoded: `MCP_PUBLIC_URL` (e.g.
  `https://host/mcp`) is the `resource` value, and `OAUTH_ISSUER_URL` (e.g. `https://host`) is the
  single entry in `authorization_servers`. **stdio mode requires neither.**
- The server still **never verifies or parses** the token (`mcp-tool-bridge.md` §9). It forwards it.
- stdio mode is unchanged and needs no OAuth: it keeps taking `REELBOLT_MCP_TOKEN`.

## 8. Exposure

- nginx proxies the MCP endpoint, the protected-resource metadata and the Go API's `/oauth/*` +
  authorization-server metadata. TLS terminates at nginx as it does for everything else.
- The MCP public origin and the Go API public origin come from config, not from a hardcoded host.

## 9. Not in scope

- No new IdP, no second permission model, no change to how the Inference API authenticates.
- No per-call pricing. Free tools stay free.
- No change to the assistant chat path's authority or to the 60 tools themselves.

## 10. Verification gate

Local evidence is the merge gate (CI is down by ruling). Required: `go build ./...` +
`go test ./...` under `api/`; `dotnet build` + `dotnet test`; `npm test` + `npx tsc --noEmit` under
`mcp/`; `npm run build` / test for `web/`; `docker compose config`. Plus an end-to-end OAuth run
(register → authorize → code → token → tool call) against a locally started API, or an explicit,
honest statement that it could not be run and why.

## 11. The admin-tools setting over HTTP

The per-user opt-in behind `application_users.mcp_admin_tools_enabled` is the operator's stated
requirement ("add the ability to enable admin tools in the Web portal UI under user settings"), so it
needs a user-facing read/write surface. The Go API is the authority for user management, so it owns
the route; the column itself stays Inference-API-owned like the rest of `application_users`.

Self only — a caller may read and change **their own** setting and nobody else's. There is no
`{userId}` in the path for exactly that reason; adding one would create an admin surface nobody asked
for.

```
GET   /api/v1/auth/settings   -> 200 { "mcpAdminToolsEnabled": boolean, "displayName": string }
PATCH /api/v1/auth/settings   -> body { "mcpAdminToolsEnabled"?: boolean, "displayName"?: string (trimmed, 1..100, no control/invisible chars; 400 otherwise; re-issues the session cookies) }
                              -> 200 { "mcpAdminToolsEnabled": boolean, "displayName": string }   (the stored values)
```

- Authenticated with the existing session; `PATCH` writes only the presented field and returns what it
  actually stored, so the UI never has to guess.
- Changing it does **not** retroactively alter tokens already issued: `assistant.admin` is granted at
  consent time, and the bridge reads the live column per call. Both are intentional — the scope is an
  audit record of what was agreed, the column is the authority.
