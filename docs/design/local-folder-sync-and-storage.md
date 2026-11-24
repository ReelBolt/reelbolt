# Design: computer folder sync, plan storage limits, uploads without a size cap

Status: approved for implementation (lead design, 2026-10-11). Branch `feat/local-folder-sync`.
Requested by the owner: iCloud Drive support → because Apple offers no API for a web service to read a
person's iCloud Drive, sync **any folder on the person's computer** through the ReelBolt desktop app,
live; plus storage limits per plan and no per-file upload cap.

Three parts, built together:

1. **Computer folder sync** — the desktop app keeps a chosen local folder mirrored into a project
   (iCloud Drive via iCloud for Windows, OneDrive, Dropbox, Box, Proton, a NAS share, or a plain folder:
   anything the OS shows as a folder). Windows and Linux only — no macOS desktop app yet (D26). Live: a change on disk reaches the project within seconds while the app runs; changes
   made while it was off are caught up when it starts.
2. **Plan storage limits, enforced everywhere and shown to people** — Trial 1 GB, Creator 15 GB,
   Pro 50 GB (Team stays 100 GB, Enterprise unlimited).
3. **No per-file upload cap** — large files go straight to object storage in parts; the storage quota is
   the only ceiling.

## Lessons carried over (rules)

From the GitHub/cloud sync work (docs/design/cloud-drive-sync.md): a zero-byte file is a file; no sync
inside an HTTP request; progress is saved as it goes; a gone source folder never mass-deletes; one
unreadable file is skipped and counted, not a failure of everything.

## Part 1 — Computer folder sync

### Model

One-way, computer → project, like the GitHub and cloud links: files in the folder are created in the
project, rewritten in place when their content changes (same file id, so nothing referencing them
breaks), and deleted when they disappear from the folder. Files a person uploaded, an agent wrote, or
another source wrote are never touched. Nothing is ever written to the computer.

A project may have **several** folder links (from one or several computers). Each link lands in its own
**target folder** inside the project (default: the local folder's name, e.g. `Footage`), so two links can
never collide. Files a link wrote carry `project_files.source_kind = 'local_folder'`,
`source_link_id = <link id>` (new nullable column) and `source_revision = <SHA-256 hex>`.

### Data (Inference API, migration `AddLocalFolderSync`)

* `project_local_folder_links`: `id`, `project_id` (FK cascade), `organization_id`, `device_id`
  (FK `runner_devices`, restrict), `target_folder` (project-relative, validated with
  `GitHubIncludePaths.TryNormalize` rules, unique per project), `display_path` (what the computer calls
  the folder, ≤ 512 chars, display only), `created_by_user_id` (the device's `PairedByUserId` at link
  time), `paused_at`, `last_reconciled_at`, `last_change_at`, `file_count`, `last_error`, `created_at`,
  `updated_at`. No tenant query filter (child of `projects`, reached through the project / device org);
  add it to `TenantBoundaryInventoryTests` as a child table.
* `project_files.source_link_id` (uuid, nullable, FK → `project_local_folder_links` ON DELETE SET NULL,
  indexed). Unlinking keeps the files (same rule as GitHub); their `source_link_id` becomes null and
  they become ordinary files.

### Device authentication (signed HTTP, no bearer token)

The desktop app already holds a per-device Ed25519 key and already calls the API base URL for pairing
(`runner/internal/pairing`, `APIBaseURL`). Folder sync uses the **same base URL** and signs every
request with the same key — the gateway WebSocket is not involved, so folder sync works whether or not
the compute fabric is deployed.

Headers on every `/api/v1/device/**` request:

```
X-ReelBolt-Device:    <deviceId>
X-ReelBolt-Timestamp: <RFC 3339 UTC>
X-ReelBolt-Nonce:     <base64url, ≥16 random bytes>
X-ReelBolt-Signature: <base64url Ed25519 signature>
```

Signed string (UTF-8):
`reelbolt-device-request-v1|<deviceId>|<timestamp>|<nonce>|<METHOD>|<path and query exactly as sent>|<lowercase hex SHA-256 of the body bytes, empty body = SHA-256 of "">`

Server (`DeviceRequestAuthenticator`, an ASP.NET authentication handler or endpoint filter on the
device controller only): reject when |now − ts| > 120 s, when `(deviceId, nonce)` was seen in the last
240 s (in-memory cache, per process — documented), when the device is unknown, revoked, not `Desktop`
kind, or has no organization, or when the signature does not verify against `runner_devices.public_key`.
Every refusal is `401 {"error":"device_unauthorized"}` with no detail (no oracle). Bodies ≤ 4 MiB.
Updates `last_seen_at` at most once a minute.

**Authority.** A device acts as **its pairing user inside its own organization**: every request
re-checks that `PairedByUserId` is still a member of the device's org and has `Edit` on the project
(a user-id-parameterised variant of the project access check — never the token-based `ICurrentUser`).
Pairing user gone or demoted → `403 {"error":"device_owner_lacks_access"}` with a plain message. A
project outside the device's org is `404` (D5).

### Device API (Inference API, `DeviceFolderSyncController`, JSON camelCase)

| Method | Path | Body → answer |
|---|---|---|
| GET | `/api/v1/device/me` | → `{ deviceId, deviceName, organizationId, organizationName, pairedByEmail }` |
| GET | `/api/v1/device/projects` | → `[{ id, name }]` projects in the device's org the pairing user can edit |
| GET | `/api/v1/device/folder-links` | → this device's links: `[{ id, projectId, projectName, targetFolder, displayPath, paused, fileCount, lastReconciledAt, lastError }]` |
| POST | `/api/v1/device/folder-links` | `{ projectId, targetFolder?, displayPath }` → `201` link. `409 target_folder_taken`, `400 invalid_target_folder` |
| DELETE | `/api/v1/device/folder-links/{id}` | → `204` (files stay) |
| POST | `/api/v1/device/folder-links/{id}/manifest` | `{ manifestId, batch, final, files: [{ path, size, sha256 }] }` (≤ 2,000 files per batch) → after the `final` batch: `{ upload: [path], delete: [path], skipped: [{path, reason}], paused, storage: { usedBytes, limitBytes } }`; non-final batches → `{ accepted: n }` |
| POST | `/api/v1/device/folder-links/{id}/uploads` | `{ path, size, sha256, contentType? }` → a `DirectUploadTicket` (single PUT or multipart). `413 STORAGE_QUOTA_EXCEEDED` with used/limit |
| POST | `/api/v1/device/folder-links/{id}/uploads/parts` | `{ ticket, partNumbers }` → `[{ partNumber, url, expiresAtUnix }]` |
| POST | `/api/v1/device/folder-links/{id}/uploads/complete` | `{ ticket, parts: [{ partNumber, etag }] }` → `{ fileId, path }` |
| POST | `/api/v1/device/folder-links/{id}/uploads/abort` | `{ ticket }` → `204` |
| POST | `/api/v1/device/folder-links/{id}/delete` | `{ paths: [..] }` (≤ 1,000) → `{ deleted: n }` |

Rules:

* **Paths** are relative to the local folder, `/`-separated, validated like `GitHubIncludePaths`
  (no `.`/`..`/empty segments, no backslash, no control chars, ≤ 1,024 chars); the stored path is
  `<targetFolder>/<path>`. A path occupied by a row from another source is skipped (`occupied`).
* **Uploads** go through `IDirectUploadService` (`Services/Uploads/IDirectUploadService.cs`) with
  `CallerKey = "device:{deviceId}"`, `SourceKind = "local_folder"`, `SourceLinkId`, `SourceRevision = sha256`
  and `ReplacesFileId` when the path already has this link's row — so a changed file keeps its id.
  An unchanged file (same `sha256` as the row's `source_revision`) is answered `409 unchanged` and needs
  no upload. Where the storage supports it, the presigned PUT carries `x-amz-checksum-sha256` so storage
  itself verifies the content (R2 and AWS do; when not supported, the claimed hash is stored as is).
* **Manifest reconciliation** (the catch-up after start/reconnect, and a safety pass every 15 min): the
  server diffs the complete manifest against the link's rows: `upload` = paths missing or with another
  hash; `delete` = link rows not in the manifest. **Mass-delete guard:** if the final manifest is empty
  while the link has files, or would delete more than 50% of a link holding ≥ 20 files, the server does
  **not** return deletions; it sets `paused = true` and `last_error` = "The folder on <device> looks empty
  or mostly gone — sync is paused so nothing is deleted. If you really removed those files, resume it
  from the desktop app." The desktop app shows this and offers Resume, which sends the manifest with
  `confirmLargeDelete: true`.
* **Live changes** go straight to `uploads`/`delete` without a manifest.
* **Paused** links (by the person, or by the guard) refuse uploads/deletes with `409 link_paused`.
* Every write updates `last_change_at`/`file_count`; reconciliation updates `last_reconciled_at`.
* Pulled files are not queued for the summarizer or the embedder (same reason as GitHub).

### Dashboard API (project members; `View` to read, `Edit` to change)

* `GET /api/v1/projects/{projectId}/folder-links` → `[{ id, deviceId, deviceName, deviceOnline (last_seen_at < 2 min), deviceRevoked, targetFolder, displayPath, paused, fileCount, lastChangeAt, lastReconciledAt, lastError }]`
* `POST /api/v1/projects/{projectId}/folder-links/{id}/pause` / `/resume` → `200` link
* `DELETE /api/v1/projects/{projectId}/folder-links/{id}` → `204` (files stay; the app stops syncing it on its next request, which answers `404`)

Links are **created only from the desktop app** (the folder picker is local).

### Desktop app (Go, `runner/`)

* **`internal/foldersync`** (new): the engine. Per link: an fsnotify watcher (new dependency
  `github.com/fsnotify/fsnotify`, BSD-3) adding a watch per directory (recursive by walking; new
  directories are added as they appear); events debounced (2 s quiet) and coalesced per path; a file is
  uploaded only after its size and mtime are stable for 2 s (still being written otherwise). A local index
  (`os.UserCacheDir()/ReelBolt/folder-index/<linkId>.json`: path → size, mtime, sha256, last synced
  sha256) avoids re-hashing unchanged files. Full rescan + manifest at start, after wake/reconnect, and
  every 15 minutes; if more than 20,000 directories would need watches, fall back to a rescan every
  2 minutes and say so in the status.
* **Skipped locally** (never uploaded, counted in status): hidden files and folders (leading `.`), `.DS_Store`,
  `Thumbs.db`, `desktop.ini`, `~$*`, `*.tmp`, `*.part`, `*.crdownload`, symlinks (not followed), and
  **online-only placeholders that are not on disk**: on Windows, files with
  `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`/`RECALL_ON_OPEN`/`OFFLINE` (iCloud for Windows, OneDrive and
  Dropbox "online-only"), shown as "only in the cloud, not downloaded to this PC — right-click the folder
  and choose 'Always keep on this device'".
  Reading such a file would trigger a download; the app never does that silently.
* **Uploads**: through the ticket — single PUT, or parts PUT in parallel (3 at a time), each part retried
  up to 3 times with backoff; on failure the upload is aborted and retried later. Bandwidth is not
  throttled in v1 but uploads pause while the governor's schedule says "don't work" only if the person
  turns on "Sync only when the app may work" (default off: sync runs whenever the app runs).
* **Requests**: `internal/deviceapi` (new): signs requests as above with `internal/identity`, base URL
  from the pairing config. A `401` stops syncing and shows "This computer is no longer connected to
  ReelBolt — pair it again"; `403 device_owner_lacks_access` shows the plain message; `413` pauses that
  file with "Storage is full (X of Y used)". 
* **Local config**: links in `os.UserConfigDir()/ReelBolt/folders.json` (0600): `linkId`, `localPath`,
  `projectId`, `targetFolder`, `paused`. The local path is never sent except as the display string.
* **CLI** (headless `reelbolt-runner`): `folders list`, `folders add <path> --project <id> [--target <name>]`,
  `folders remove <linkId>`, `folders pause|resume <linkId>`; `run` starts the sync engine alongside the
  job loop.
* **Desktop window** (`internal/ui` + `frontend/`, Wails host in `cmd/reelbolt-runner-desktop`): a
  **Folders** section: list of synced folders (local path, project, status: Up to date / Syncing N files /
  Paused / Needs attention with the reason, last change), **Add folder** (native folder picker → choose
  project from `/device/projects` → target folder name, prefilled) , Pause/Resume, Remove (confirm: "files
  already copied stay in the project"), and "Open project in browser". Tray menu shows a one-line sync
  status. **Windows and Linux only** (decision D26 stands, owner 2026-10-11: no macOS desktop app for
  now). iCloud is covered on Windows through Apple's iCloud for Windows, which exposes iCloud Drive as a
  folder in File Explorer.

## Part 2 — Plan storage limits

* **Catalog** (`inference/.../Billing/plans.catalog.json` and the byte-identical `site/lib/pricing/plans.catalog.json`,
  both edited together): `trial` 1, `creator` 15 (was 25), `pro` 50 (was 100), `team` 100 (unchanged,
  per workspace), `enterprise` unlimited (absent). Add a storage line to each priced plan's `highlights`
  ("15 GB of storage", …) so the pricing page and dashboard say it. Update
  an operator document §storage. Prices are unchanged ($29 / $79 / $49 per seat).
* **What counts**: the existing definition — the sum of `project_files.size_bytes` over the workspace's
  projects (uploads, synced files from every source, agent files, workflow outputs). Fix the wrong comment
  in `PlanGateService` that says outputs are not counted. Image-editor working layers and render
  scratch have no `project_files` row and are not counted (documented).
* **Enforced on every path that adds or grows a row**, all through `IPlanGateService.CheckUploadAsync`
  with the **net** growth (new size − old size when rewriting):
  - browser multipart + direct upload (already), and the new multipart direct upload;
  - text edit `PUT .../files/{id}/content`; assistant `WriteProjectFile`;
  - GitHub pull and cloud-drive sync: before writing each new/changed file; when it would not fit, the
    file is skipped and the sync ends with a plain `LastError` "Storage is full (X of Y used) — N files
    were not copied. Free space or upgrade, then sync again." Deletions and unchanged files still apply.
  - computer folder sync (via the upload ticket, above).
  - **Not blocked:** workflow outputs and agent-written files (the run is already paid for in credits);
    they still count, so the workspace must free space before adding more. Nothing is ever deleted
    automatically, including after a downgrade below current usage.
* **Shown to people**: `GET /api/v1/billing/summary` gains `storage: { usedBytes, limitBytes }`
  (`limitBytes` null = unlimited) and a new `GET /api/v1/storage/usage` (any member) returns the same plus
  `byProject: [{ projectId, name, usedBytes }]` (top 20). Dashboard: a usage bar on the Billing page and in
  Settings ("4.2 GB of 15 GB used"), a warning banner on project pages at ≥ 90 %, and every
  `STORAGE_QUOTA_EXCEEDED` (upload tray, assistant attach, sync errors) shown as "Your workspace is out of
  storage (X of Y used). Delete files or upgrade your plan." with a link to Billing.

## Part 3 — Uploads without a size cap

* `IDirectUploadService` (contract committed) implemented in `Services/Uploads/DirectUploadService.cs`
  by extracting today's single-PUT logic from `ProjectFilesController` (ticket MAC, server-chosen key,
  HEAD verification, quota) and adding multipart: `CreateMultipartUpload` at ticket time (server),
  presigned `UploadPart` URLs in batches (≤ 100 per call, TTL 4 h), `CompleteMultipartUpload` +
  HEAD at completion, `AbortMultipartUpload` on abort/refusal. Part size `DirectUploadLimits.PartSizeFor`.
* Browser routes (`ProjectFilesController`, unchanged paths, extended bodies):
  - `POST .../files/upload-url` → ticket now includes `mode` (`singlePut`|`multipart`), `uploadId`,
    `partSizeBytes`, `partCount` (and `url` for single PUT). `maxBytes` = 5 TiB.
  - `POST .../files/upload-parts` `{ ticket echo, partNumbers }` → part URLs. (new)
  - `POST .../files/upload-complete` `{ ticket echo, sizeBytes, parts? }` (parts required for multipart).
  - `POST .../files/upload-abort` `{ ticket echo }` → 204. (new)
* `MaxDirectUploadBytes` 20 GB → `DirectUploadLimits.MaxObjectBytes`; update `DirectUploadTests` and
  `web/lib/api/files.upload.test.ts`. The multipart **form** route (`POST .../files`, the self-host
  fallback) keeps its proxy-imposed 100 MB cap (Cloudflare Free); its error says so plainly.
* Web (`web/lib/api/files.ts`): multipart tickets upload parts with 3 concurrent XHR PUTs, aggregate
  progress, read `ETag` (exposed by the R2 CORS policy), retry a failed part up to 3 times, abort on
  cancel/failure. Single-PUT path unchanged.
* Fix stale doc lines (`docs/direct-upload.md` says the nginx route raises its cap — it does not).
* Abandoned multipart uploads are aborted by the existing R2 lifecycle rule (1 day); document the
  equivalent rule for AWS/Garage.

### The D5 exception: the assistant's upload URL (`ProposeUploadFile`)

D5 (launch-film-platform-gaps) says nothing that returns media to the assistant or the MCP may carry a
storage key, a bucket or a presigned S3 URL. `ProposeUploadFile` is the one sanctioned exception: an
MCP client has no browser session, so the bytes must go straight to storage. Accepted for this tool
only (QA-P0-12, S-05), on these terms, each enforced in code and tested:

* **Write-only.** The URL is a presigned `PUT` (single object, or one `UploadPart` per part). It cannot
  read, list or delete anything. Nothing the assistant can view is ever returned as a presigned URL.
* **Server-chosen key.** The key is derived server-side from the project, a fresh object id and the
  sanitised path (`DirectUploadService.DeriveKey`), under `projects/{projectId}/userFiles/`. The client
  never names it. The answer carries no key or bucket anywhere except inside the PUT URL itself, not
  even in the opaque `uploadToken` (`AssistantUploadToolsTests.With_a_real_presigner_the_key_and_bucket_appear_only_inside_the_PUT_url`).
* **Short TTL.** A single-PUT URL lives at most 15 minutes and a part URL at most 30 (the browser's
  routes keep 4 hours). The ticket MAC covers the short-lived flag (`RelocateOnComplete`), so a client
  cannot strip it.
* **Moved on Apply.** `ApplyUploadFile` moves the object (server-side copy, then delete) to a fresh key
  that was never presigned, then HEADs and quota-checks that copy and records it. A later re-PUT through
  a still-live URL lands on the old key, which no row points at. A second Apply returns the same file
  (never a second row) and deletes any such stray object. One server-side `CopyObject` caps these
  uploads at 5 GiB; larger files go through the web app.
* **Never logged.** The URL is not written to any log or trace by the API. The approval card shows the
  file name and size only. The URL does reach the model provider and the chat transcript, which is why
  it is short-lived and useless once applied.

## Ownership (build team)

| Ticket | Owner | Scope |
|---|---|---|
| A. Uploads + storage limits (backend) | provider-google | `Services/Uploads/**`, `ProjectFilesController` upload routes, quota on every path (GitHub, cloud, text edit, assistant write), catalog ×2 + highlights, billing summary + `/storage/usage`, `PlanGateService`, tests |
| B. Folder sync (backend) | cloud-core | migration `AddLocalFolderSync`, `DeviceRequestAuthenticator`, `DeviceFolderSyncController`, `ProjectFolderLinksController`, reconcile/guard, user-id project access, tests; then **docs** (ticket E) |
| C. Folder sync (desktop app) | graph-provider-dev | `runner/internal/foldersync`, `runner/internal/deviceapi`, CLI, `internal/ui` + `frontend/` Folders section, desktop host folder picker, Go tests |
| D. Web | github-sync-fix | multipart browser upload, storage usage bar/banner/quota errors, project page "Folders on your computers" list (pause/resume/unlink), tests |
| E. Docs | cloud-core (after B) | `docs/local-folder-sync.md`, an operator document (incl. iCloud: "Keep Downloaded"), storage limits in the plans user guide, `docs/direct-upload.md` multipart, CLAUDE.md paragraph, desktop-runner docs |

## Out of scope

Two-way sync; bandwidth limits; syncing while the app is closed (impossible by construction — the
dashboard says when a computer is offline); a native iCloud API (none exists).
