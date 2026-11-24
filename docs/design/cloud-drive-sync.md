# Design: syncing a project with Google Drive, OneDrive/SharePoint and Dropbox

Status: approved for implementation (lead design, 2026-10-11). Branch `feat/cloud-file-sync`.

This is the working design for the build team. The operator/developer reference and the user guide are
written from it (`docs/cloud-drive-sync.md`, an operator document).

## What the feature does

A project can mirror **one folder** from each of three file-sharing platforms, in addition to (and
independently of) a linked GitHub repository:

| Provider | Covers | API |
|---|---|---|
| Google Drive | My Drive and shared drives | Drive API v3 |
| OneDrive / SharePoint | personal OneDrive, OneDrive for Business, SharePoint document libraries | Microsoft Graph v1.0 |
| Dropbox | the personal space, and a team member's team space | Dropbox API v2 |

The model is the GitHub integration's, deliberately (`docs/github-app.md`), so a person who knows one
knows the other:

1. A person **connects an account** (OAuth authorization code + PKCE). The connection is theirs, in the
   active workspace; tokens are encrypted at rest.
2. On a project they can edit, they **link a folder** from a connected account, optionally narrowing it to a
   selection of files/sub-folders (same `includePaths` format and rules as GitHub).
3. A **sync** makes the project's files from that provider equal the folder (one-way, provider → project):
   new file → created; changed revision → rewritten in place (same id); gone or deselected → deleted;
   unchanged revision → nothing downloaded. Files from uploads, agents, GitHub or another provider are
   never touched (an occupied path is skipped and counted).
4. Syncs run **in the background**. "Sync now", linking, and changing the selection only *queue* a sync and
   answer `202` at once. The dashboard shows "Syncing…" while one runs. A periodic sweep re-syncs every
   link (`CloudStorage:SweepIntervalMinutes`, default 30, 0 disables), because none of the three platforms
   pushes to us without extra webhook infrastructure (webhooks/delta are a follow-up, see the end).

Read-only everywhere: ReelBolt never writes to, moves or deletes anything on the platform.

## Lessons from the GitHub sync failure (2026-10-11) that are now rules

* **A zero-byte file is a file.** GitHub returned `web/public/.gitkeep` with empty content; the client
  treated empty content as a broken response, raised a fake "502", and every pull failed forever. Every
  provider has a test listing and downloading an empty file, and the sync service has one writing it.
* **No sync inside an HTTP request.** A pull of a few hundred files outlived nginx's 60 s and the browser
  (nginx logged 499) and the aborted request cancelled the pull. Every sync runs on the background worker
  with its own cancellation, never `HttpContext.RequestAborted`.
* **Progress is saved as it goes.** Rows are saved every 25 written files, so an interrupted sync keeps what
  it wrote and the next one resumes (revision match = no download). Deleting vanished files and stamping
  `LastSyncedAt` happen only after a complete listing and a complete pass.
* **A failure says what actually failed.** One file that cannot be read is skipped and counted, not a
  "temporary server problem" for the whole sync. Only an auth/server/rate-limit failure fails the sync.

## Data model (Inference API owns; one migration `AddCloudStorageSync`)

Entities are in `inference/src/ReelBolt.Shared/Data/Models/CloudStorage.cs` (written by the lead; the
comments there are the field-level spec):

* `cloud_storage_connections` — `CloudStorageConnection`. Unique (user_id, organization_id, provider, account_id).
  Owner-scoped: every query filters on the caller's user **and** active organization; anything else is
  `404`. No tenant query filter (addressed only together with the caller's own user id) — update
  `TenantBoundaryInventoryTests` if it enumerates child/unfiltered tables.
* `cloud_storage_oauth_states` — `CloudStorageOAuthState`. Unique state_hash. 10-minute TTL, single use.
* `project_cloud_links` — `ProjectCloudLink`. Unique (project_id, provider). `connection_id` FK with
  `ON DELETE SET NULL`; `project_id` FK cascade. `include_paths_json` jsonb.
* `project_files` is **unchanged**: a synced file has `source_kind` = `CloudStorageProviders.SourceKind(kind)`
  (`google_drive` / `onedrive` / `dropbox`) and `source_revision` = the provider revision. One link per
  provider per project means the source kind identifies the link.

Provider enum stored as int (append-only). WorkflowEngine does not map the new tables.

## Provider abstraction (lead-written contract, do not change signatures without the lead)

`inference/src/ReelBolt.Inference.Api/Services/CloudStorage/`:

* `ICloudStorageProvider.cs` — the interface + records + `CloudStorageApiException`/`CloudStorageErrorKind`.
  Its doc comment lists the contract every implementation keeps.
* `CloudStorageOptions.cs` — `CloudStorage:*` configuration, vendor URLs (overridable), limits, redirect URI.
* `CloudHttp.cs` — the one send path: retries 429/5xx with `Retry-After` (cap 30 s, 4 attempts), maps
  statuses to kinds, `classify` hook for provider-specific bodies, `Esc()` for URL components.
  `CloudHttp.Delay` is overridable so tests do not sleep.
* `Providers/GoogleDriveStorageProvider.cs`, `Providers/MicrosoftGraphStorageProvider.cs`,
  `Providers/DropboxStorageProvider.cs` — stubs with the fixed constructor
  `(HttpClient, IOptions<CloudStorageOptions>, ILogger<T>)` and `HttpClientName` constant.

### Google Drive specifics

* Scopes: `https://www.googleapis.com/auth/drive.readonly`. Authorize with `access_type=offline`,
  `prompt=consent` (otherwise no refresh token on re-consent), `include_granted_scopes=true`, PKCE S256.
  Token exchange / refresh at `TokenUrl` (form body). Google does not rotate refresh tokens.
* Account: `GET /drive/v3/about?fields=user(permissionId,emailAddress,displayName)`; account id = `permissionId`.
* Drives: My Drive (`DriveId` null, `Personal`) + `GET /drive/v3/drives` (paged, `SharedDrive`).
* Every files call: `supportsAllDrives=true`; listing inside a shared drive also `includeItemsFromAllDrives=true`,
  `corpora=drive`, `driveId=…`. Root folder id: `root` for My Drive, the drive id for a shared drive.
* Listing: `GET /drive/v3/files?q='{id}' in parents and trashed=false&fields=nextPageToken,files(id,name,mimeType,size,md5Checksum,version,modifiedTime)&pageSize=1000`
  (escape `'` and `\` in the id inside the q string). Tree = breadth-first over folders.
* Revision: `md5Checksum` when present, else `v{version}`.
* Native Google files are exported, not downloaded (`files/{id}/export?mimeType=`), extension appended
  to the path: Docs → `text/markdown` `.md`; Sheets → `text/csv` `.csv` (first sheet); Slides →
  `application/pdf` `.pdf`; Drawings → `image/png` `.png`. Revision `v{version}`. Size 0 (unknown).
  Every other `application/vnd.google-apps.*` (forms, sites, maps, jamboards, **shortcuts**) is
  Unsupported. Download: `files/{id}?alt=media`.
* Rate limits: `403` with reason `rateLimitExceeded`/`userRateLimitExceeded` → `RateLimited` (classify hook).
* Revoke: `POST RevokeUrl` form `token=…`.
* Production note for docs: `drive.readonly` is a *restricted* scope; a public deployment needs Google's
  OAuth verification (and a security assessment). Unverified ("testing") apps work for up to 100 listed users.

### OneDrive / SharePoint specifics (Microsoft Graph)

* Scopes: `offline_access User.Read Files.Read.All Sites.Read.All`. Authorize/token at
  `{AuthorityBaseUrl}/{Tenant}/oauth2/v2.0/authorize|token`, PKCE S256, `response_mode=query`.
  Microsoft rotates refresh tokens: store the new one.
* Account: `GET /me?$select=id,displayName,mail,userPrincipalName`; account id = `id`; email = mail ?? UPN.
* Drives: `GET /me/drive` (`Personal`, `DriveId` null) + `GET /me/followedSites?$select=id,displayName`
  then `GET /sites/{id}/drives?$select=id,name,driveType` (`SharePointLibrary`, name "Site — Library").
  A personal Microsoft account cannot call followedSites: treat 400/403/404 there as "no SharePoint", not an error.
* Items: `/me/drive/…` when `DriveId` null, else `/drives/{driveId}/…`; folder `root` or `items/{id}`.
  Children: `…/children?$select=id,name,size,file,folder,package,remoteItem,cTag,eTag,lastModifiedDateTime&$top=200`,
  follow `@odata.nextLink` (only if it is on `GraphBaseUrl`'s host). Tree = breadth-first.
* Revision: `cTag` (content tag) when present, else `eTag`. `package` items (OneNote notebooks) and
  `remoteItem` (shared-with-me shortcuts) are Unsupported.
* Download: `…/items/{id}/content` answers 302 to a pre-authenticated URL. Do **not** let HttpClient send the
  bearer token to that host: disable auto-redirect on the named client (core registers it with
  `AllowAutoRedirect = false`), read `Location`, require https, GET it without Authorization.
* Revoke: no endpoint for v2 tokens — no-op.

### Dropbox specifics

* Authorize with `token_access_type=offline`, `response_type=code`, PKCE S256, scope
  `account_info.read files.metadata.read files.content.read`. Token/refresh at `{ApiBaseUrl}/oauth2/token`.
  Dropbox does not rotate refresh tokens.
* Account: `POST /2/users/get_current_account` (body `null`, `Content-Type: application/json`); id = `account_id`.
* Drives: the home (`DriveId` null). If `root_info.root_namespace_id != root_info.home_namespace_id`
  (team member), add the team space (`DriveId` = root namespace id, `TeamSpace`). Calls into a drive set
  `Dropbox-API-Path-Root: {".tag":"namespace_id","namespace_id":"…"}`.
* Folder id = Dropbox file id (`id:…`, survives renames); root = `""` (id null). `files/get_metadata` to resolve
  a folder's `path_display`.
* Tree: `POST /2/files/list_folder` `{path, recursive:true, include_deleted:false, include_non_downloadable_files:true, limit:2000}`
  then `/list_folder/continue` with the cursor. Relative path = `path_display` minus the root folder's
  `path_display` prefix (compare on `path_lower`). Revision `content_hash` (else `rev`).
  `is_downloadable == false` (Paper docs, etc.) → Unsupported.
* Download: `POST {ContentBaseUrl}/2/files/download`, header `Dropbox-API-Arg: {"path":"id:…"}` — header JSON
  must be ASCII: escape every non-ASCII char as `\uXXXX`.
* Errors: Dropbox answers `409` with `error_summary` like `path/not_found/…` → `NotFound` (classify hook);
  `401` `expired_access_token` → `Unauthorized`; `429` → `RateLimited`.
* Revoke: `POST /2/auth/token/revoke` with the access token.

## Core services (cloud-core ticket)

* **Registration** (`InferenceApiServiceCollectionExtensions`, its own "Cloud storage" block): bind
  `CloudStorageOptions`; a named `HttpClient` per provider (timeout `HttpTimeoutSeconds`; the OneDrive one
  with `AllowAutoRedirect = false`); the three providers as singletons, also exposed as
  `IEnumerable<ICloudStorageProvider>`; `ICloudStorageProviderRegistry` (`Get(kind)`, `Configured`).
* **`CloudStorageConnectionService`** — OAuth start (state + PKCE, hashed/encrypted, 10 min, returnPath
  validated as a same-origin `/app/…` path) and callback (consume once, exchange, account lookup, upsert by
  account id, encrypt tokens); `GetAccessTokenAsync(connection)` refreshing when expiry is < 2 minutes away
  (store a rotated refresh token; a refused refresh sets `connection.LastError` and throws
  `Unauthorized`); disconnect (best-effort revoke, delete; links get `connection_id` null).
* **`CloudSyncService.SyncAsync(linkId, force, ct)`** — the generic reconcile (shape of
  `GitHubRepositoryPullService.ReconcileAsync`): access token (on `Unauthorized` from a call: refresh once and
  retry once); `ListTreeAsync(max = MaxFilesPerSync + 1)`; truncated → refused `too_many_files`; selection
  via `GitHubIncludePaths`; also skip `.DS_Store`, `Thumbs.db`, `desktop.ini`, `~$*` Office lock files; limits
  (`too_many_files`, `selection_too_large` with the two numbers); occupancy and revision rules as GitHub;
  download streaming with the `MaxFileBytes` cap enforced while reading; per-file `NotFound`/`Forbidden` →
  skip+count, continue; any other provider error → fail the sync. Save every 25 written files. After a full
  pass: delete vanished rows + objects, stamp `LastSyncedAt`, `LastSyncedFileCount`, clear `LastError`.
  Plain-language `LastError` by kind (reconnect / no access / folder gone / rate limited / provider problem
  / too many files…). Pulled files: `summary_status = Done`, `indexing_status = NotIndexed` (no LLM bill), as GitHub.
* **`CloudSyncQueue` + `CloudSyncWorker`** (`BackgroundService`): `Channel<CloudSyncRequest(LinkId, Force)>`;
  de-duplicates a link already queued/running; `IsSyncing(linkId)` for the API; manual cooldown 20 s per link
  (`429 sync_too_soon` + `Retry-After`); 2 syncs concurrently; each in its own DI scope; stopping-token
  cancellation only. **`CloudSyncSweeper`**: every `SweepIntervalMinutes`, queue links whose `LastSyncedAt`
  is older than the interval (or null), skipping links with no connection. Per-process (multi-replica runs
  are idempotent wasted work, never wrong work).

## HTTP API (cloud-core ticket)

All JSON camelCase; errors `{ "error": code, "message": plain words }`.

`/api/v1/cloud-storage` (authenticated):

| Method | Path | |
|---|---|---|
| GET | `providers` | `[{ provider: "google-drive", displayName, configured }]` for all three |
| GET | `connections` | caller's connections in the active org: `id, provider, accountEmail, accountDisplayName, createdAt, lastError` |
| DELETE | `connections/{id}` | `204`; `404` if not the caller's |
| POST | `{provider}/oauth/start` | body `{ returnTo }` → `{ authorizeUrl }`; `503 provider_not_configured` / `public_base_url_missing` |
| GET | `{provider}/oauth/callback` | browser redirect from the vendor; redirects to `returnTo` (default `/app/settings`) with `?cloud=connected&provider=…` or `?cloud=error&provider=…&reason=…` |
| GET | `connections/{id}/drives` | `[{ driveId, name, kind }]` |
| GET | `connections/{id}/folders?driveId=&folderId=` | `{ folder: { folderId, name, displayPath }, items: [{ id, name, isFolder, size, modifiedAt }], truncated }` (max 500 items) |

`/api/v1/projects/{projectId}/cloud-links` (project `Edit` permission via `IProjectAccessService`; `View` for GET):

| Method | Path | |
|---|---|---|
| GET | `` | `[{ id, provider, connectionId, accountEmail, driveId, folderId, folderDisplayPath, includePaths, lastSyncedAt, lastSyncedFileCount, lastError, syncing }]` |
| POST | `` | `{ connectionId, driveId?, folderId?, includePaths? }` → `201` link (first sync queued). Connection must be the caller's; folder validated with `GetFolderAsync`. `409 already_linked`, `404 connection_not_found`/`folder_not_found`, `400 invalid_include_paths` |
| PATCH | `{linkId}` | `{ includePaths? }` → `200`; queues a forced sync only if the normalized selection changed |
| DELETE | `{linkId}` | `204`; files stay (same rule as unlinking GitHub) |
| POST | `{linkId}/sync` | `202 { status: "queued" | "already_syncing" }`; `429 sync_too_soon` |

Ids from requests (`driveId`, `folderId`) are validated (≤ 1024 chars, no control characters) before use.
`provider` route values parse with `CloudStorageProviders.TryParseSlug`; anything else is `404`.

## Configuration (cloud-core ticket: `docker-compose.yml` inference env + `.env.example`)

```
CloudStorage__PublicBaseUrl: ${PUBLIC_BASE_URL:-}
CloudStorage__GoogleDrive__ClientId: ${GOOGLE_DRIVE_CLIENT_ID:-}
CloudStorage__GoogleDrive__ClientSecret: ${GOOGLE_DRIVE_CLIENT_SECRET:-}
CloudStorage__OneDrive__ClientId: ${MICROSOFT_GRAPH_CLIENT_ID:-}
CloudStorage__OneDrive__ClientSecret: ${MICROSOFT_GRAPH_CLIENT_SECRET:-}
CloudStorage__OneDrive__Tenant: ${MICROSOFT_GRAPH_TENANT:-common}
CloudStorage__Dropbox__ClientId: ${DROPBOX_APP_KEY:-}
CloudStorage__Dropbox__ClientSecret: ${DROPBOX_APP_SECRET:-}
CloudStorage__SweepIntervalMinutes: ${CLOUD_SYNC_SWEEP_MINUTES:-30}
```

Redirect URIs to register: `{PUBLIC_BASE_URL}/api/v1/cloud-storage/{google-drive|onedrive|dropbox}/oauth/callback`.
No credential exists for any vendor yet: nothing may require one to build or test, and "not configured" is
a tested first-class state.

## Dashboard (cloud-web ticket)

* `web/lib/types/cloudStorage.ts`, `web/lib/api/cloudStorage.ts` (mirror `web/lib/api/github.ts`).
* Project page: a **"Cloud storage"** tab (`?tab=cloud`) next to the GitHub repository tab, one card per
  configured provider: not connected → "Connect {provider}" (POST start, then `window.location = authorizeUrl`
  with `returnTo` = this tab); connected, not linked → choose account → drive → folder (lazy tree via
  `folders`, reuse the GitHub tab's file/folder selection component and `web/lib/github/selection.ts`
  rules if practical) → Link; linked → folder path, account, last synced (time ago), file count, last
  error in plain words with the fix ("Reconnect", "Choose fewer files"), "Sync now", "Change files", "Unlink".
  While any link has `syncing: true`, refetch the list every 3 s; stop when none is syncing. Show the
  `?cloud=connected|error` return as a notification once, then strip the query.
* Settings page: "Connected accounts" section listing connections (provider, email, problem badge) with Disconnect.
* Unconfigured providers are not shown; if none is configured the tab is hidden.
* No internal codes in copy; plain language.

## Docs (cloud-docs ticket)

`docs/cloud-drive-sync.md` (operator + developer: what it does, vendor app setup step by step for Google
Cloud / Microsoft Entra / Dropbox App Console incl. exact redirect URIs and scopes, env vars, limits, the
security rules above, Google verification note), an operator document (plain language),
index entries in `docs/README.md` and the user-guide index/sidebar, one paragraph in `CLAUDE.md`. Every `##`
section stands alone; `npm run build` in `docs-site` must pass.

## Out of scope (follow-ups)

Push notifications / change feeds (Drive `changes.watch`, Graph subscriptions, Dropbox webhooks) to replace
the sweep; two-way sync; Google Picker with the narrower `drive.file` scope; Box.
