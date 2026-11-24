# Cloud drive sync (Google Drive, OneDrive/SharePoint, Dropbox)

A ReelBolt project can mirror **one folder** from each of three file-sharing platforms — Google Drive,
OneDrive/SharePoint and Dropbox — in addition to, and independently of, a linked GitHub repository
([`github-app.md`](github-app.md)). This document is the developer and operator reference: what a sync
does to a project's files, how syncs run, the security rules, how to create the three vendor apps, the
configuration, the limits, the HTTP API and what each error means. The person-facing guide is
[`user-guide/cloud-drive-sync.md`](user-guide/projects/cloud-drive-sync.md); the design record is
[`design/cloud-drive-sync.md`](design/cloud-drive-sync.md).

The code lives in `inference/src/ReelBolt.Inference.Api/Services/CloudStorage/` (the provider contract
`ICloudStorageProvider`, `CloudHttp`, `CloudStorageOptions`, the three providers under `Providers/`, and
the generic `CloudStorageConnectionService`, `CloudSyncService`, `CloudSyncQueue`, `CloudSyncWorker`,
`CloudSyncSweeper`), plus `Controllers/CloudStorageController.cs` and
`Controllers/ProjectCloudLinksController.cs`. The feature is inert until configured: a provider without
a client id and secret is hidden from the dashboard and its endpoints answer `503 provider_not_configured`.

## What cloud drive sync does, in one paragraph

A person connects an account on Google Drive, OneDrive/SharePoint or Dropbox (OAuth, read-only scopes).
On a project they can edit, they link one folder of that account — optionally narrowed to chosen files
and sub-folders — and ReelBolt copies those files into the project's file library and keeps them equal to
the folder. The sync is **one-way, provider to project**, and **read-only everywhere**: ReelBolt never
writes to, moves, renames or deletes anything on the platform. A project has at most one linked folder
per provider (so up to three, plus a GitHub repository), and each one is synced in the background:
on linking, on a change of the chosen files, on "Sync now", and periodically by a sweep.

## What a cloud sync does to a project's files

A cloud sync makes the project's files **from that provider** equal the linked folder (restricted to the
chosen files). It is a reconciliation, keyed on the provider's revision of each file — the same rules as a
GitHub pull ([`github-app.md`](github-app.md), "What \"pull\" means for a ReelBolt project"):

| In the folder | What happens in the project |
|---|---|
| A file that is new | A project file is created (category `userFiles`, path relative to the linked folder). |
| A file whose revision changed | The existing project file is rewritten in place; its id stays the same. |
| A file that is gone, or no longer chosen | The project file and its stored object are deleted — only after a complete listing and a complete pass. |
| A file whose revision is unchanged | Nothing: no download, no upload. |
| A zero-byte file | Written like any other file, with size 0. |
| A file that is gone or forbidden by the time it is downloaded | Skipped and counted; the sync completes. |
| A file larger than `MaxFileBytes` (declared, or discovered while streaming) | Skipped and counted. |
| `.DS_Store`, `Thumbs.db`, `desktop.ini`, `~$…` Office lock files | Never synced. |
| A name containing a backslash or a control character | Skipped and counted (a backslash is part of a name on the platform, never a folder separator). |
| A path a person uploaded, an agent wrote, GitHub or another provider synced | Nothing. The path is occupied; the file is counted as skipped. |

A synced row carries `project_files.source_kind` = `google_drive`, `onedrive` or `dropbox`
(`CloudStorageProviders.SourceKind`) and `source_revision` = the provider's revision (a content hash where
the platform has one: Google `md5Checksum`, Dropbox `content_hash`, Graph `cTag`; else a version or etag;
a revision longer than 100 characters is stored as `sha256:<hex>` of it). Because a project has at most
one link per provider, the source kind alone names the link a file belongs to, and a sync never reads,
rewrites or deletes a row with any other source kind. Synced files are stored with
`summary_status = Done` and `indexing_status = NotIndexed`: a background sync never starts the summarizer
or the embedder, so it never runs up an LLM bill nobody asked for.

Google's native documents have no bytes to download, so they are **exported** and the export's extension is
appended to the name: Google Docs → Markdown (`.md`), Sheets → CSV of the first sheet (`.csv`), Slides →
PDF (`.pdf`), Drawings → PNG (`.png`). Forms, Sites, My Maps, Jamboards and shortcuts are not
downloadable and are left out. OneNote notebooks and "shared with me" shortcuts on OneDrive, and Paper
documents and other non-downloadable files on Dropbox, are left out the same way. Two Google Drive files
with the same name in one folder map to one path: the one with the smallest item id is synced and the
others are skipped.

## Cloud syncs run in the background

No cloud sync runs inside an HTTP request — the lesson of the GitHub "Sync now" that outlived nginx's
60 seconds and was cancelled with the aborted request (see [`github-app.md`](github-app.md), "Syncs run in
the background", which now works the same way). Linking a folder, changing the chosen files and "Sync now"
only **queue** a sync and answer at once (`201`, `200`, `202`).

`CloudSyncQueue` (a singleton, in-memory channel) holds at most one entry per link: a link already queued
or running is not queued again, and the API reports `already_syncing`. A non-manual request made while a
sync of that link is running — a changed selection — makes it run once more afterwards, so the project
ends at the new selection; a "Sync now" during a running sync adds nothing. "Sync now" has a 20-second
cooldown per link (`429 sync_too_soon` with `Retry-After`). `GET …/cloud-links` reports `syncing: true`
while a sync of the link is queued or running; the dashboard re-reads the list every 3 seconds while any
link is syncing.

`CloudSyncWorker` (a `BackgroundService`) runs two syncs at a time, each in its own DI scope under the
system tenant scope, cancelled only by the host's stopping token. `CloudSyncSweeper` re-syncs every link
every `CloudStorage:SweepIntervalMinutes` (default 30; `0` disables it), because none of the three platforms
pushes changes to ReelBolt without webhook infrastructure. The sweep queues links whose last successful sync
is older than the interval (or that never synced), and skips links with no connection (they must be linked
again), links whose connection is marked broken (the person must reconnect first; reconnecting clears the
mark), links of an unconfigured provider, and links already queued or running. A sweep-queued sync is not
forced: it does nothing for a link that synced successfully less than a minute ago. Queue, cooldown and
sweep are per API process: a restart drops queued syncs (the next sweep or "Sync now" repairs the project,
because a sync reconciles by revision), and several replicas would each sweep — idempotent wasted work,
never wrong work.

## A cloud sync saves its progress as it goes

`CloudSyncService` saves the rows it has written every 25 files (`CloudSyncService.SaveEvery`), and also
saves whatever it has written when it fails or is stopped. Each saved row carries the revision it came
from, so an interrupted sync — an API restart, a provider failure half-way — keeps its work and the next
sync skips those files as unchanged. What needs a complete pass waits for one: deleting files that left
the folder or the selection, and stamping `last_synced_at`, `last_synced_file_count` and `last_error = null`
on the link. A sync whose listing fails — the folder deleted, trashed or no longer visible, an expired
connection, a rate limit — deletes nothing at all.

## Which cloud failures fail a sync, and which only skip a file

One file that cannot be read is skipped and counted, not a failure of the whole sync: a per-file
`NotFound` or `Forbidden` on download, a file over `MaxFileBytes`, or a read that breaks mid-stream. Only a
failure of the listing or of the account fails the sync: `Unauthorized` (after one refresh and one retry),
`RateLimited` (after `CloudHttp` gave up retrying), `ServerError`, a malformed answer (`InvalidResponse`),
or the folder itself being gone. The reason is written to `project_cloud_links.last_error` in plain
language (see "Troubleshooting cloud drive sync"); the operator detail goes to the log. Every provider call
goes through `CloudHttp.SendAsync`, which retries `429` and `500/502/503/504` up to four attempts, honouring
`Retry-After` (capped at 30 s), then maps what is left to a `CloudStorageApiException` with a
`CloudStorageErrorKind`.

## Security rules of cloud drive sync

- **Read-only scopes.** Google `drive.readonly`; Microsoft `offline_access User.Read Files.Read.All
  Sites.Read.All`; Dropbox `account_info.read files.metadata.read files.content.read`. Nothing ReelBolt
  holds can write to a drive.
- **Connections are owner-scoped.** A connection belongs to one person in one workspace. Every lookup
  filters on the caller's user id **and** active organization; anybody else's connection — another
  person's, or the same person's in another workspace — is the same `404 connection_not_found`, so the API
  is never an existence oracle. A folder can only be linked with the caller's own connection, in the
  project's workspace. Links are children of projects and every link route goes through the project access
  check.
- **Tokens are encrypted at rest** with ASP.NET Core Data Protection (`ISecretProtector`, the shared
  `dpkeys` volume), never returned by the API, never logged and never put into an exception message.
- **OAuth: authorization code + PKCE (S256), a hashed single-use state, bound to the person.** The `state`
  is 32 random bytes, stored only as its SHA-256; the PKCE verifier is stored encrypted; the ticket lives
  10 minutes and is consumed once. The ticket is bound to the person and workspace that started it **and**
  to the provider. The callback is reached by a top-level navigation from the vendor, which carries the
  session cookie (`SameSite=Lax`); it completes only when the signed-in caller is the person who started
  the flow, and otherwise redirects with `reason=session_mismatch` **without** consuming the ticket. That
  binding stops somebody from sending a victim a consent link that would connect the victim's drive to the
  attacker's ReelBolt account. Who is connected, and in which workspace, is read from the ticket, never
  from the URL.
- **Open-redirect proof callback.** The callback always redirects to a same-origin path taken from the
  ticket — validated when it was stored: `/app` or `/app/…`, safe characters only, no traversal, no encoded
  separators, no fragment, at most 512 characters; anything else falls back to `/app/settings`.
- **Ids are validated before use.** `driveId` and `folderId` from a request are at most 1024 characters
  with no control characters (`400 invalid_id`), and every provider percent-escapes what it puts into a URL.
- **The Graph download redirect never receives the bearer token.** Microsoft Graph answers a download with
  `302` to a pre-authenticated URL on another host. The OneDrive `HttpClient` is registered with
  `AllowAutoRedirect = false`; the provider reads `Location`, requires `https`, and fetches it **without**
  an `Authorization` header. Graph's paging links are followed only when they are on `GraphBaseUrl`'s host.
- **Token refresh.** An access token expiring within two minutes is refreshed before use, and a call
  answered `401` is refreshed once and retried once. Microsoft rotates refresh tokens and the new one is
  stored; Google and Dropbox do not rotate. A refused refresh (`invalid_grant`) writes a plain-language
  `last_error` on the connection ("reconnect"), and every sync of a link using it fails with "reconnect".
  Refreshes of one connection are serialized within a process.

## Create the Google Drive app (operator)

1. In the [Google Cloud console](https://console.cloud.google.com/), create or choose a project.
2. **APIs & Services → Library**: enable the **Google Drive API**.
3. **APIs & Services → OAuth consent screen**: choose **External** (or **Internal** for a Google Workspace
   organization that only serves its own users), fill in the app name, support e-mail and authorized
   domain (the deployment's), and add the scope `https://www.googleapis.com/auth/drive.readonly`.
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**, application type
   **Web application**. Under **Authorized redirect URIs** add exactly
   `{PUBLIC_BASE_URL}/api/v1/cloud-storage/google-drive/oauth/callback`.
5. Copy the client id and client secret into `GOOGLE_DRIVE_CLIENT_ID` and `GOOGLE_DRIVE_CLIENT_SECRET`.

`drive.readonly` is a **restricted scope**. While the consent screen's publishing status is **Testing**,
only the test users you list (up to 100) can connect, and their grants expire after seven days. A public
deployment needs Google's OAuth app verification for a restricted scope, which includes a security
assessment; plan for it before opening the feature to everyone. ReelBolt authorizes with
`access_type=offline`, `prompt=consent` and `include_granted_scopes=true`, so every connection receives a
refresh token.

## Create the Microsoft Entra app for OneDrive and SharePoint (operator)

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), **App registrations → New
   registration**.
2. **Supported account types**: "Accounts in any organizational directory and personal Microsoft accounts"
   to offer both OneDrive for Business/SharePoint and personal OneDrive (matching
   `MICROSOFT_GRAPH_TENANT=common`); choose a narrower type and tenant if the deployment only serves one
   organization.
3. **Redirect URI**: platform **Web**, value exactly
   `{PUBLIC_BASE_URL}/api/v1/cloud-storage/onedrive/oauth/callback`.
4. **Certificates & secrets → New client secret**; copy its **Value** (not its id) into
   `MICROSOFT_GRAPH_CLIENT_SECRET`, and the **Application (client) ID** into `MICROSOFT_GRAPH_CLIENT_ID`.
   A client secret expires (at most 24 months): put a reminder in the calendar to rotate it.
5. **API permissions → Add a permission → Microsoft Graph → Delegated permissions**: `offline_access`,
   `User.Read`, `Files.Read.All`, `Sites.Read.All`.

`MICROSOFT_GRAPH_TENANT` is `common` (work, school and personal accounts), `organizations`, `consumers` or
one tenant id. `Files.Read.All` and `Sites.Read.All` are delegated permissions that many organizations
only allow after **admin consent**: if a work account sees "Need admin approval", a tenant administrator
grants consent once for the organization (API permissions → "Grant admin consent", or the consent URL).
A personal Microsoft account has no SharePoint; ReelBolt then simply offers only its OneDrive.

## Create the Dropbox app (operator)

1. In the [Dropbox App Console](https://www.dropbox.com/developers/apps), **Create app**: **Scoped
   access**, access type **Full Dropbox** (an "App folder" app could only see its own folder).
2. **Permissions** tab: tick `account_info.read`, `files.metadata.read`, `files.content.read`, then
   **Submit**. Scopes chosen after a person connected only apply when they connect again.
3. **Settings** tab → **OAuth 2 → Redirect URIs**: add exactly
   `{PUBLIC_BASE_URL}/api/v1/cloud-storage/dropbox/oauth/callback`.
4. Copy the **App key** into `DROPBOX_APP_KEY` and the **App secret** into `DROPBOX_APP_SECRET`.

A new Dropbox app is in **development** status: only a limited number of users (500) can connect
before you apply for **production** status from the app's settings page. A team member's team space
appears next to their personal space automatically.

## Cloud drive sync configuration: environment variables

All are set on the **inference** service (`docker-compose.yml` passes them through; `.env.example`
documents them).

| `.env` variable | Config key | Secret | Meaning |
|---|---|---|---|
| `PUBLIC_BASE_URL` | `CloudStorage__PublicBaseUrl` | no | The public origin. Builds every redirect URI; without an absolute http(s) URL, connecting answers `503 public_base_url_missing`. |
| `GOOGLE_DRIVE_CLIENT_ID` | `CloudStorage__GoogleDrive__ClientId` | no | Google "Web application" OAuth client id. |
| `GOOGLE_DRIVE_CLIENT_SECRET` | `CloudStorage__GoogleDrive__ClientSecret` | **yes** | Its secret. |
| `MICROSOFT_GRAPH_CLIENT_ID` | `CloudStorage__OneDrive__ClientId` | no | Entra Application (client) ID. |
| `MICROSOFT_GRAPH_CLIENT_SECRET` | `CloudStorage__OneDrive__ClientSecret` | **yes** | The client secret's value. |
| `MICROSOFT_GRAPH_TENANT` | `CloudStorage__OneDrive__Tenant` | no | `common` (default), `organizations`, `consumers` or a tenant id. |
| `DROPBOX_APP_KEY` | `CloudStorage__Dropbox__ClientId` | no | Dropbox app key. |
| `DROPBOX_APP_SECRET` | `CloudStorage__Dropbox__ClientSecret` | **yes** | Dropbox app secret. |
| `CLOUD_SYNC_SWEEP_MINUTES` | `CloudStorage__SweepIntervalMinutes` | no | Background re-sync interval in minutes (default 30); `0` disables the sweep. |

Each provider is all-or-nothing: with its client id or secret missing it is hidden
(`configured: false`) and answers `503 provider_not_configured`. The redirect URIs to register, byte for
byte:

```
{PUBLIC_BASE_URL}/api/v1/cloud-storage/google-drive/oauth/callback
{PUBLIC_BASE_URL}/api/v1/cloud-storage/onedrive/oauth/callback
{PUBLIC_BASE_URL}/api/v1/cloud-storage/dropbox/oauth/callback
```

The provider slugs in these URLs (`google-drive`, `onedrive`, `dropbox`) and the source kinds stored on
files never change once shipped. Vendor endpoints are overridable for testing
(`CloudStorage__GoogleDrive__AuthorizeUrl`/`TokenUrl`/`RevokeUrl`/`ApiBaseUrl`,
`CloudStorage__OneDrive__AuthorityBaseUrl`/`GraphBaseUrl`,
`CloudStorage__Dropbox__AuthorizeUrl`/`ApiBaseUrl`/`ContentBaseUrl`); there is no reason to change them
in production. Secrets belong in the same store as the other credentials — see [`secrets.md`](secrets.md).

## Cloud drive sync limits

| Limit | Default | Config key | What happens over it |
|---|---|---|---|
| Files per sync | 2,000 | `CloudStorage__MaxFilesPerSync` | A listing over it (or a selection over it) is refused, `too_many_files`; nothing changes. |
| Bytes per file | 50 MB | `CloudStorage__MaxFileBytes` | The file is skipped, enforced on the declared size and again while streaming. |
| Bytes per sync | 512 MB | `CloudStorage__MaxTotalBytes` | The selection is refused, `selection_too_large`, with both numbers. |
| HTTP timeout per call | 300 s | `CloudStorage__HttpTimeoutSeconds` | The provider call fails. |
| Sweep interval | 30 min | `CloudStorage__SweepIntervalMinutes` | — |
| Chosen paths per link | 500 paths, 512 characters each | (fixed, `GitHubIncludePaths`) | `400 invalid_include_paths`. |
| Items per folder listing | 500 | (fixed) | The picker shows `truncated`. |
| "Sync now" cooldown | 20 s per link | (fixed) | `429 sync_too_soon`. |
| Concurrent syncs | 2 per API process | (fixed) | Queued. |
| OAuth state lifetime | 10 min | (fixed) | The callback reports `expired`. |

## Cloud drive sync HTTP API: connections

All JSON is camelCase; errors are `{ "error": code, "message": plain words }`. `provider` is the slug.

| Method | Path (`/api/v1/cloud-storage/…`) | Answers |
|---|---|---|
| `GET` | `providers` | `200 [{ provider, displayName, configured }]` for all three. |
| `GET` | `connections` | `200 [{ id, provider, accountEmail, accountDisplayName, createdAt, lastError }]` — the caller's connections in the active workspace. Carries no token. |
| `DELETE` | `connections/{id}` | Best-effort revoke at the provider, then delete; links that used it keep their files and get no connection. `204`, or `404 connection_not_found`. |
| `POST` | `{provider}/oauth/start` | Body `{ returnTo }` (a `/app/…` path; anything else is ignored). `200 { authorizeUrl }`; `503 provider_not_configured` / `public_base_url_missing`; `404 provider_not_found` (unknown slug) / `no_workspace` (no active organization). |
| `GET` | `{provider}/oauth/callback` | Browser redirect from the vendor; always a `302` (never JSON) to `returnTo` (default `/app/settings`) plus `cloud=connected&provider=…` or `cloud=error&provider=…&reason=…`. |
| `GET` | `connections/{id}/drives` | `200 [{ driveId, name, kind }]`, the account's own space first (`driveId` null). `kind`: `personal`, `sharedDrive`, `sharePointLibrary`, `teamSpace`. |
| `GET` | `connections/{id}/folders?driveId=&folderId=` | `200 { folder: { folderId, name, displayPath }, items: [{ id, name, isFolder, size, modifiedAt }], truncated }`, folders first, at most 500 items. `driveId` absent = the own space, `folderId` absent = the root. |

Errors of the browse routes: `404 connection_not_found` (not the caller's), `400 invalid_id`,
`404 folder_not_found` (gone, or not visible to that account — one answer for both),
`409 reconnect_required` (the provider refused the token after one refresh),
`424 provider_error` (rate limit, provider failure or an unexpected answer; `424`, never `502`, for the
reason given in [`github-app.md`](github-app.md)), `503 provider_not_configured`.

Callback `reason` values: `denied` (the person cancelled consent), `invalid_state` (unknown, spent or
foreign state, or no code), `expired` (older than 10 minutes), `session_mismatch` (not signed in, or
signed in as somebody other than the person who started the flow), `exchange_failed` (the vendor refused
the code, or the stored verifier could not be decrypted), `account_unavailable` (the account could not be
read after the exchange), `not_configured`.

## Cloud drive sync HTTP API: project links

All routes go through the project access check: `View` for `GET`, `Edit` for the rest; another
workspace's project is `404`.

| Method | Path (`/api/v1/projects/{projectId}/cloud-links…`) | Answers |
|---|---|---|
| `GET` | (the list itself) | `200 [{ id, provider, connectionId, accountEmail, driveId, folderId, folderDisplayPath, includePaths, lastSyncedAt, lastSyncedFileCount, lastError, syncing }]`. `includePaths` null = every file. |
| `POST` | (the list itself) | Body `{ connectionId, driveId?, folderId?, includePaths? }`. The connection must be the caller's own, in the project's workspace; the folder is validated with the provider. `201` with the link (`syncing: true`, first sync queued). `409 already_linked` (one link per provider per project), `404 connection_not_found` / `folder_not_found`, `400 invalid_include_paths` / `invalid_id`, `409 reconnect_required`, `424 provider_error`, `503 provider_not_configured`. |
| `PATCH` | `/{linkId}` | Body `{ includePaths? }`: null leaves the selection alone, `[]` means every file. `200` with the link; a forced sync is queued only when the normalized selection changed. `400 invalid_include_paths`, `404 link_not_found`. |
| `DELETE` | `/{linkId}` | `204`; the synced files stay. `404 link_not_found`. |
| `POST` | `/{linkId}/sync` | "Sync now": `202 { status: "queued" }` or `202 { status: "already_syncing" }`; `429 sync_too_soon` with `Retry-After` and `retryAfterSeconds`; `404 link_not_found`. |

`includePaths` uses exactly the GitHub selection's format and rules (`GitHubIncludePaths`):
folder-relative paths, a folder includes everything under it (also files added later), normalized,
de-duplicated and sorted before they are stored.

## Cloud drive sync data model

| Table | Notes |
|---|---|
| `cloud_storage_connections` | One person's connected account in one workspace: provider (int, append-only enum), the provider's stable account id (never the email), display e-mail/name, encrypted access and refresh tokens, expiry, granted scopes, `last_error`. Unique `(user_id, organization_id, provider, account_id)`. No tenant query filter: owner-scoped by every query. |
| `cloud_storage_oauth_states` | Single-use OAuth tickets: SHA-256 of the state (unique), encrypted PKCE verifier, user, organization, provider, validated return path, `expires_at`, `consumed_at`. Spent and expired rows are removed when the same person starts another flow. |
| `project_cloud_links` | One folder per `(project_id, provider)` (unique). `connection_id` → connections `ON DELETE SET NULL` (the link and its files outlive a disconnected account); `project_id` → projects `ON DELETE CASCADE`. `drive_id`, `folder_id` (stable ids, survive renames), `folder_display_path` (display only), `include_paths_json` (jsonb), `last_synced_at`, `last_synced_file_count`, `last_error`. No tenant query filter: a child of the project, reached through the project access check. |
| `project_files.source_kind` / `source_revision` | Unchanged columns: `google_drive` / `onedrive` / `dropbox` and the provider revision mark a synced row. |

All three tables belong to the Inference API (migration `AddCloudStorageSync`, history table
`__EFMigrationsHistory_Api`); the WorkflowEngine does not map them. `TenantBoundaryInventoryTests` lists
them as unfiltered tables.

## Cloud drive provider specifics

- **Google Drive** (Drive API v3): My Drive plus shared drives. Every files call sends
  `supportsAllDrives=true` (and inside a shared drive `includeItemsFromAllDrives=true`, `corpora=drive`).
  Listing is breadth-first over folders, 1,000 items per page. Revision: `md5Checksum`, else `v{version}`.
  A `403` with `rateLimitExceeded`/`userRateLimitExceeded` is a rate limit. Disconnecting revokes the token
  at Google. Google does not rotate refresh tokens.
- **OneDrive / SharePoint** (Microsoft Graph v1.0): the person's OneDrive plus the document libraries of
  the SharePoint sites they follow (shown as "Site — Library"); a personal account has no SharePoint.
  Revision: `cTag`, else `eTag`. Downloads follow Graph's `302` without the bearer token (see the security
  rules). Microsoft rotates refresh tokens. There is no revocation endpoint: disconnecting deletes the
  tokens; the person can also remove the app under their Microsoft account's app permissions.
- **Dropbox** (API v2): the personal space, plus the team space for a team member (calls into it carry
  `Dropbox-API-Path-Root`). The folder id is Dropbox's `id:…`, which survives renames. Listing is one
  recursive `list_folder` with cursors. Revision: `content_hash`, else `rev`. A `409 path/not_found` is
  `NotFound`. Disconnecting revokes the token at Dropbox. Dropbox does not rotate refresh tokens.

## Troubleshooting cloud drive sync

The `last_error` sentences below are what a person sees on the project's **Cloud storage** tab; the
provider's name replaces "Google Drive" where it applies.

| The link or connection says | Cause and fix |
|---|---|
| "Google Drive no longer accepts ReelBolt's access to the account this folder was linked with. Reconnect the account in Settings, then sync again." | The token was refused even after a refresh: access was removed at the provider, the grant expired (Google "Testing" apps expire grants after 7 days), or the password changed. The person reconnects the same account; links keep working without being re-created. |
| Connection: "… no longer accepts ReelBolt's access to this account … Reconnect the account in Settings." | A refresh was refused (`invalid_grant`). Reconnecting clears it, and the sweep resumes the links. |
| "The account this folder was linked with is no longer allowed to open it on Google Drive …" | The folder's owner stopped sharing it with that account. Share it again, or link another folder. |
| "The linked folder could not be found on Google Drive …" | The folder was deleted, trashed or unshared. Nothing was deleted from the project; link another folder (unlink, then link). |
| "Google Drive is limiting how many requests ReelBolt may make right now …" | The provider's rate limit outlasted the retries. Wait a few minutes; the next sweep retries on its own. |
| "Google Drive had a problem answering …" | A `5xx` after retries. Usually temporary. |
| "Google Drive answered in a way ReelBolt did not expect …" | A malformed or unexpected answer (`InvalidResponse`); the log has the REST path and status. Retry; if it persists, unlink and link again, and report it. |
| "Google Drive syncing is not set up on this server …" | The provider's client id/secret was removed after the link was made. Restore them. |
| "The Google Drive account this folder was linked with has been disconnected. Link the folder again." | The connection was disconnected; the link has no connection. Connect an account and link the folder again. |
| "The linked folder holds more than 2000 files …" / "Too many files are chosen to sync …" | Over `MaxFilesPerSync`. The person chooses fewer files or a smaller folder; raising the limit is the operator's alternative. |
| "The chosen files are too large to sync together …" | Over `MaxTotalBytes`; same fix. |
| "The saved choice of files to sync could not be read …" | The stored selection is unreadable; choose the files again. |
| "The sync stopped because of a problem on ReelBolt's side …" | An unexpected exception (storage, database); the log has it. Nothing was deleted. |

| Operator symptom | Cause |
|---|---|
| A provider is missing from the dashboard | Its client id or secret is empty on the **inference** service (`GET /api/v1/cloud-storage/providers` shows `configured: false`). |
| Connecting answers `503 public_base_url_missing` | `PUBLIC_BASE_URL` is not an absolute http(s) URL on the inference service. |
| The vendor says the redirect URI does not match | The registered redirect URI and `{PUBLIC_BASE_URL}/api/v1/cloud-storage/{slug}/oauth/callback` differ (scheme, host, path, trailing slash). |
| The callback lands with `reason=session_mismatch` | The person finished the consent in a browser or profile where they are not signed in to ReelBolt (or as another user), or their session expired during consent. Sign in and start again. |
| `reason=exchange_failed` | The vendor refused the code (wrong client secret, redirect URI mismatch, code reused), or the `dpkeys` volume changed so the verifier could not be decrypted. |
| Google: "This app is blocked" / "access_denied" for a user | The consent screen is in Testing and the user is not a listed test user, or the restricted-scope app is not verified. |
| Microsoft: "Need admin approval" | The tenant requires admin consent for `Files.Read.All`/`Sites.Read.All`. |
| Files are missing from the project | They are outside the selection, are OS/Office litter, are over `MaxFileBytes`, are not downloadable (forms, shortcuts, OneNote, Paper), have a backslash in their name, or their path is already used by an upload, an agent's file, GitHub or another provider. |
| A synced Google Doc shows up as `.md` | Expected: native Google documents are exported (Docs → `.md`, Sheets → `.csv`, Slides → `.pdf`, Drawings → `.png`). |

## What cloud drive sync deliberately does not do

- **It never writes to the platform**: no upload, move, rename or delete, and the scopes could not do it.
- **No push notifications or change feeds yet.** Drive `changes.watch`, Graph subscriptions and Dropbox
  webhooks would replace the sweep; until then changes arrive on the next sweep or "Sync now".
- **No two-way sync**, and no conflict handling: the folder is the source of truth for the files it wrote.
- **No Google Picker with the narrower `drive.file` scope.** It would avoid the restricted-scope
  verification but needs the person to pick every file through Google's own UI.
- **No Box** (or other providers) yet.
- **Synced files are not summarized or embedded** automatically, as with GitHub.
- **One folder per provider per project.** A second folder of the same provider would make the source kind
  ambiguous.
