# Computer folder sync (a folder on a computer → a ReelBolt project)

A ReelBolt **computer folder sync** keeps a folder on a person's own computer copied into a project, in
one direction, while the ReelBolt desktop app runs. The person points the app at any folder the operating
system shows as a folder, and every file in it (minus the skips below) is created in the project under its
own target folder; changed files are rewritten in place, and files that leave the folder are removed from
the project. Nothing is ever written back to the computer.

This page is the developer and operator reference: the rules a sync follows, the mass-delete guard, what is
skipped, how the desktop app authenticates, the device and dashboard HTTP APIs, the storage limits it
enforces, the upload paths, the data model, the desktop commands, and what each error means. The
person-facing guide is [`user-guide/local-folder-sync.md`](user-guide/projects/local-folder-sync.md); the design
record is [`design/local-folder-sync-and-storage.md`](design/local-folder-sync-and-storage.md).

The feature exists because **Apple offers no API for a web service to read a person's iCloud Drive**.
On Windows, Apple's own iCloud for Windows exposes iCloud Drive as an ordinary folder in File Explorer, so
it syncs like any other folder. OneDrive, Dropbox, Box, a NAS share and a plain directory work the same
way. The desktop app is **Windows and Linux only** (decision D26); there is no macOS desktop app.

The code lives in the Inference API (`Services/FolderSync/`, `Controllers/DeviceFolderSyncController.cs`,
`Controllers/ProjectFolderLinksController.cs`, `Controllers/StorageUsageController.cs`,
`Services/Uploads/`, `Services/Billing/StorageUsageService.cs`) and in the Go runner
(`runner/internal/foldersync/`, `runner/internal/deviceapi/`, `runner/cmd/reelbolt-runner/folders.go`,
`runner/internal/ui/folders*.go`). Nginx routes `/api/v1/device/` to a block of its own
(`nginx/locations.conf`): no session cookie is translated, the staging basic-auth front door is opted out,
and the body cap is 4 MiB, because the only credential is the device signature.

## The rules a folder sync follows

A link is **one-way, computer → project**. The rules are the same ones the GitHub pull and cloud-drive sync
follow ([`cloud-drive-sync.md`](cloud-drive-sync.md)):

- **A new file** in the folder is created in the project, category `userFiles`, at
  `<targetFolder>/<path relative to the local folder>`.
- **A changed file is rewritten in place** — the project file keeps its id, so a workflow step or an
  editorial document that references it never breaks. The new bytes land beside the old object and the row
  is pointed at them only after the upload is verified.
- **A file that leaves the folder** (deleted, renamed, moved out) is removed from the project, with its
  stored object.
- **A file a person uploaded, an agent wrote, GitHub pulled or another cloud drive synced is never
  touched**, even if it has the same path. A path already held by another source is skipped as `occupied`.
- **Nothing is written to the computer**, at any point. The sync only reads.
- **A zero-byte file is a file**: it is copied like any other.
- **A project may have several links** (from one or several computers). Each link lands in its own
  **target folder** inside the project — the local folder's own name by default, e.g. `Footage` — and a
  target folder may not equal, contain or lie inside another link's folder in the same project, so two
  links can never collide.
- Files a link wrote carry `project_files.source_kind = 'local_folder'`,
  `source_link_id = <link id>` and `source_revision = <SHA-256 hex of the content>`.
- **Unlinking keeps the files.** They become ordinary project files (`source_link_id` is set to null by the
  foreign key's `ON DELETE SET NULL`), and the computer stops syncing the folder on its next request
  (which answers `404 link_not_found`).
- **A synced file is not queued for the summarizer or the embedder**, exactly as with GitHub and the cloud
  drives: a background sync must not run up an LLM bill nobody asked for.
- A link is **created only from the desktop app**, because the folder picker is local. The dashboard can
  list, pause, resume and unlink one.

## What a folder sync skips, and files that are only in the cloud

The desktop app never uploads, and the server never accepts, these paths. Each skip is counted and named in
the app's status, with a few examples, so a person can see what did not arrive:

- **Hidden files and folders** — a name starting with `.`.
- **System clutter** — `.DS_Store`, `Thumbs.db`, `ehthumbs.db`, `desktop.ini`.
- **Temporary or unfinished files** — `~$…` (Office lock files), `*.tmp`, `*.part`, `*.crdownload`.
- **Symlinks are not followed.** A link to another place is skipped, so a folder cannot be walked twice or
  escape the chosen folder.
- **Files with a name the project cannot hold** — a path over 1,024 characters, a backslash, a control
  character, an empty/`.`/`..` segment, or a segment that begins or ends with whitespace. A path is
  checked, never repaired.
- **Files that are only in the cloud.** iCloud for Windows, OneDrive and Dropbox mark a file that is not
  downloaded as an *online-only placeholder*. On Windows those files carry the
  `FILE_ATTRIBUTE_OFFLINE`, `FILE_ATTRIBUTE_RECALL_ON_OPEN` or `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`
  attribute; **reading one would make the sync client download it**, so the app never opens it. It is
  skipped and the status says so, in the words a person can act on: "Only in iCloud/OneDrive/Dropbox, not
  downloaded to this PC — right-click the folder and choose 'Always keep on this device'". The user guide
  has the fix (see [`user-guide/local-folder-sync.md`](user-guide/projects/local-folder-sync.md)).
- On Windows, a file or folder carrying the hidden or system attribute is treated as hidden too.

The skip decision is made from the directory entry's metadata alone (`Lstat` data, which is the
`FindFirstFile` record on Windows), so deciding to skip an online-only file never opens it.
One skipped file never fails the sync: the rest are copied.

## The mass-delete guard

A folder that suddenly looks empty is far more likely to be a drive that failed to mount, a cloud folder
that has not hydrated its contents, or a moved directory than it is to be a person deleting everything. A
mistaken deletion in the project cannot be undone, so the server refuses to act on it.

After the final manifest batch of a catch-up, the server compares the manifest against the link's rows and
computes what to `upload` and what to `delete`. The guard applies to the deletions:

- the manifest is **empty** while the link still holds files; or
- the manifest would delete **more than half** of a link that holds at least **20 files**
  (`LocalFolderSyncService.GuardMinimumFiles`).

When the guard applies and the computer did not explicitly confirm, the server:

1. returns **no deletions and no uploads**,
2. sets `paused = true` with `pausedReason = "guard"`, and
3. sets `lastError` to "The folder on `<device name>` looks empty or mostly gone — sync is paused so nothing is
   deleted. If you really removed those files, resume it from the desktop app."

Nothing is deleted. The pause is lifted in one of two ways:

- **From the desktop app**, which is the only side that can confirm the deletion: its Resume sends the next
  manifest with `confirmLargeDelete: true`, and the deletions then apply.
- **By itself**: the guard's pause clears on the next manifest that looks whole again, because the folder
  recovered.

A pause a person made (from the app or the dashboard) is different: nothing is reconciled at all until it
is resumed, so the computer's view of the folder is not acted on. A guard pause, by contrast, still
reconciles on every manifest — it returns the deletions held back, and it lifts itself when the folder looks
whole again. While either pause lasts, uploads and deletes are refused with `409 link_paused`. A dashboard
resume (`POST …/folder-links/{id}/resume`) clears the pause so the next manifest can be judged again; it
does **not** confirm the large deletion, which stays the desktop app's call.

## How the desktop app authenticates its requests

Folder sync works whether or not the compute fabric is deployed: it does not use the runner gateway's
WebSocket. The app already holds a per-device Ed25519 key (from pairing) and a configured API origin, and it
signs every request to `/api/v1/device/**` with that key. There is **no bearer token and no cookie**.

Each request carries four headers:

```
X-ReelBolt-Device:    <deviceId (uuid)>
X-ReelBolt-Timestamp: <RFC 3339 UTC, second precision>
X-ReelBolt-Nonce:     <base64url, at least 16 random bytes>
X-ReelBolt-Signature: <base64url Ed25519 signature, padding optional>
```

The signed string is UTF-8, and every component is exactly as the request carried it:

```
reelbolt-device-request-v1|<deviceId>|<timestamp>|<nonce>|<METHOD>|<path and query exactly as sent>|<lowercase hex SHA-256 of the body bytes>
```

An empty body hashes the empty string. The method is uppercased; the path and query are the raw request
target (`url.URL.RequestURI()` on the client, Kestrel's `RawTarget` on the server). The timestamp is
formatted with second precision, so both sides agree byte for byte.

The server (`DeviceRequestAuthenticator`, run as a resource filter in front of every action of the device
controller) refuses a request when:

- a header is missing or the device id is not a uuid;
- the timestamp is more than **120 s** away from the server's clock
  (`DeviceRequestAuthenticator.MaxSkew`);
- the nonce does not decode to at least 16 bytes;
- the body is larger than **4 MiB**;
- the device is unknown, is **revoked**, is not `Desktop` kind, or has no organization, pairing user or
  public key;
- the signature does not verify against `runner_devices.public_key` (standard base64 of the raw 32-byte
  Ed25519 key; the signature is base64url of the 64 bytes); or
- the `(deviceId, nonce)` pair was already seen within **240 s** (`DeviceNonceCache.Window`).

Every refusal is the same `401 {"error":"device_unauthorized"}` with no detail — the server never becomes an
oracle for which check failed. The single exception is a body over 4 MiB, which is
`413 {"error":"body_too_large"}`. The reason is written to the Inference API log only.

**The nonce cache is in memory and per API process.** A replay therefore only has to reach a *different*
replica within the 120-second timestamp window to be accepted there; with one API process (the normal
single-host deployment) there is no such window at all. A deployment that runs several replicas should
either route `/api/v1/device/` to one of them or accept that bound. The same in-memory caveat applies to the
manifest buffer (below). `last_seen_at` is updated at most once a minute.

**What a device may do.** A device acts as **its pairing user, inside its own organization**: every request
re-checks from the database that `PairedByUserId` (or `CreatedByUserId`) is still a member of the device's
organization and may **edit** the project. It never uses the request's `ICurrentUser`, because there is no
user token. A pairing user who left the workspace or lost edit rights is
`403 {"error":"device_owner_lacks_access"}` with a plain message; a project or link outside the device's
organization, or another device's link, is answered exactly like a missing one — `404 project_not_found` or
`404 link_not_found` (decision D5: existence never leaks).

**What a revoked device gets** is that same `401 device_unauthorized`, because "revoked" is one of the
refusal reasons. The desktop app stops syncing and shows "This computer is no longer connected to
ReelBolt — pair it again"; the dashboard shows the link with `deviceRevoked: true`. Revoking is immediate
for new requests even though an already-open gateway connection also stops receiving work.

## The device API

All of it is on the Inference API, under `/api/v1/device`, JSON camelCase, and every request is signed as
above. Errors are `{"error": code, "message": plain words}`. A paused link refuses uploads and deletes, but
`abort` still works (so the computer can clean up a half-sent upload) and a `manifest` answers with
`paused: true`.

| Method | Path | Body → answer |
|---|---|---|
| `GET` | `/api/v1/device/me` | → `{ deviceId, deviceName, organizationId, organizationName, pairedByEmail }` |
| `DELETE` | `/api/v1/device/me` | → `204`: this computer removes itself (the desktop app's Unpair); see "Removing this computer from its workspace" below |
| `GET` | `/api/v1/device/projects` | → `[{ id, name }]`: the device's organization's projects its pairing user can edit |
| `GET` | `/api/v1/device/folder-links` | → this device's links: `[{ id, projectId, projectName, targetFolder, displayPath, paused, pausedReason, fileCount, lastReconciledAt, lastError }]` |
| `POST` | `/api/v1/device/folder-links` | `{ projectId, targetFolder?, displayPath }` → `201` link. `409 target_folder_taken`, `400 invalid_target_folder` / `invalid_display_path`, `404 project_not_found`, `403 device_owner_lacks_access` |
| `DELETE` | `/api/v1/device/folder-links/{id}` | → `204` (the files stay) |
| `POST` | `/api/v1/device/folder-links/{id}/manifest` | `{ manifestId, batch, final, confirmLargeDelete?, files: [{ path, size, sha256 }] }` → a non-final batch answers `{ accepted: n }`; the final one answers `{ upload: [path], delete: [path], skipped: [{ path, reason }], paused, pausedReason, lastError, storage: { usedBytes, limitBytes } }` |
| `POST` | `/api/v1/device/folder-links/{id}/uploads` | `{ path, size, sha256, contentType? }` → a ticket (single PUT or multipart). `409 unchanged` / `occupied` / `link_paused`; `413 STORAGE_QUOTA_EXCEEDED` with `usedBytes`/`limitBytes`; `400 invalid_path` / `invalid_sha256` / `invalid_size` |
| `POST` | `/api/v1/device/folder-links/{id}/uploads/parts` | `{ ticket, partNumbers }` → `[{ partNumber, url, expiresAtUnix }]` (at most 100 per call) |
| `POST` | `/api/v1/device/folder-links/{id}/uploads/complete` | `{ ticket, parts: [{ partNumber, etag }] }` → `{ fileId, path }` |
| `POST` | `/api/v1/device/folder-links/{id}/uploads/abort` | `{ ticket }` → `204` |
| `POST` | `/api/v1/device/folder-links/{id}/delete` | `{ paths: [..] }` (at most 1,000) → `{ deleted: n }` |

Rules that hold across the table:

- **Paths** are relative to the local folder, `/`-separated, and validated with the `GitHubIncludePaths`
  rules (`FolderSyncPaths`): no empty, `.` or `..` segment, no backslash, no control character, at most
  1,024 characters, and no segment that would change under whitespace trimming. The stored project path is
  always exactly `<targetFolder>/<path>`.
- **Manifest batches** are 0-based, hold at most **2,000** files each
  (`FolderSyncManifestBuffer.MaxFilesPerBatch`) and at most **200,000** in one manifest. Batches may arrive
  in any order and a repeated batch number replaces itself. A pending manifest expires after **15 minutes**.
  The buffer is **in memory and per API process**: a manifest whose batches land on two replicas is answered
  `409 manifest_incomplete` and the computer starts a new one. Over 200,000 files is
  `413 manifest_too_large`.
- **Skipped reasons** the server reports are `occupied` (another source holds the path), `invalid_path`
  (the path fails the rules above) and `duplicate_path` (two manifest entries name the same path).
- **Every write** updates `last_change_at` and recounts `file_count`; a completed manifest updates
  `last_reconciled_at`.
- **A ticket is bound to the device** (`CallerKey = "device:{deviceId}"`) and to the link: a ticket this
  server issued to this device for another link or project is refused (`400 invalid_ticket`).
- All refusals other than the `401`/`403`/`404` family are the upload machinery's own codes, listed under
  "Uploads without a per-file size cap" and in the troubleshooting table below.

## Removing this computer from its workspace

When a person unpairs the desktop app, the app calls `DELETE /api/v1/device/me` (signed like every device
request) before it forgets its own pairing, so the old computer does not linger in the workspace until
someone removes it from the dashboard. The Inference API (`DeviceSelfRemoval`) does exactly what the
dashboard's removal (`DELETE /api/v1/runners/{id}` on the Go API) does: the `runner_devices` row is kept and
stamped `revoked_at`, which takes it off the workspace's device list, makes every later request the computer
signs a `401 device_unauthorized`, and makes the runner gateway drop its connection at the next 60-second
`revoked_at` re-check. `revoked_by_user_id` is left empty, because the computer removed itself and no person
did. Its folder links stay, shown in the dashboard as belonging to a removed computer, and the files they
synced stay in their projects, exactly as after a removal from the dashboard.

The call is allowed whatever the pairing user may still do in the workspace (giving up access never needs
access). A repeat after a lost answer cannot authenticate any more and gets `401`, which the app treats as
"already removed"; a server that predates the route answers `404`, and the app then tells the person to
remove the computer from the dashboard. The runner's client is `(*deviceapi.Client).ForgetMe`.

## The dashboard API for folder links

The project's own members manage links through `ProjectFolderLinksController`, mounted at
`/api/v1/projects/{projectId}/folder-links`. `View` is required to read and `Edit` to change; another
workspace's project is `404`. A link is looked up only within that project. Links are never created here —
only the desktop app can, because the folder picker is local.

| Method | Path | Answers |
|---|---|---|
| `GET` | `/api/v1/projects/{projectId}/folder-links` | `200 [{ id, deviceId, deviceName, deviceOnline, deviceRevoked, targetFolder, displayPath, paused, pausedReason, fileCount, lastChangeAt, lastReconciledAt, lastError }]` |
| `POST` | `…/folder-links/{id}/pause` | `200` link; the computer's uploads and deletes are refused with `409 link_paused` until it is resumed |
| `POST` | `…/folder-links/{id}/resume` | `200` link. Resuming a link the mass-delete guard paused only lets the next manifest be judged again; the large deletion is confirmed from the desktop app |
| `DELETE` | `…/folder-links/{id}` | `204`; the files stay. The computer stops syncing on its next request (`404`) |

`deviceOnline` is true when the device was seen within **2 minutes**
(`ProjectFolderLinksController.OnlineWindow`), and false for a revoked device. `pausedReason` is `"user"`
(a person paused it) or `"guard"` (the mass-delete guard did).

## Storage limits per plan

Every workspace has a storage limit from its plan. The plan catalog
(`inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json`, mirrored byte-identically at
`site/lib/pricing/plans.catalog.json`) carries it as `entitlements.storageGb`, and each priced plan's
highlights state it in words:

| Plan | Storage | `storageGb` |
|---|---|---|
| Trial | 1 GB | 1 |
| Creator | 15 GB | 15 |
| Pro | 50 GB | 50 |
| Team | 100 GB per workspace | 100 |
| Enterprise | unlimited | absent (`UnlimitedInt`) |

**What counts** is the workspace's `project_files.size_bytes` summed over all its projects: uploads, files
synced from GitHub, a cloud drive or a computer folder, files agents wrote, and rendered workflow outputs.
There is exactly one definition of the number — `IStorageUsageService` — used by the plan gate, the billing
summary and `GET /api/v1/storage/usage`, so the number a person sees is the number the quota enforces.
Image-editor working layers and render scratch have no `project_files` row and are **not** counted.

**Where it is enforced.** Every path that adds or grows a row checks the **net growth** (new size minus the
size of the file being rewritten) through `IPlanGateService.CheckUploadAsync`:

- browser multipart upload and the direct upload routes (including a multipart direct upload);
- the text edit `PUT …/files/{id}/content` and the assistant's `WriteProjectFile`;
- a GitHub pull and a cloud-drive sync, before each new or changed file is written: a file that does not fit
  is skipped and counted, deletions and unchanged files still apply, and the sync ends with a plain
  `last_error` ("Storage is full (X of Y used) — N files were not copied. Free space or upgrade, then sync
  again."); and
- a computer folder sync, through the upload ticket.

**What happens when the storage is full.** A new or growing write is refused with
`413 STORAGE_QUOTA_EXCEEDED` and the violation's message ("This upload would exceed the `<plan>` plan's
`<limit>` GB storage quota. Delete files or upgrade your plan."), carrying the used and limit bytes. A folder
sync pauses that file with "Storage is full (X of Y used)" and tries the rest.

**Nothing is ever deleted automatically.** Reaching the limit does not trigger a cleanup, a retention sweep
or an eviction, and a downgrade that puts a workspace above its new limit does not delete anything either:
the workspace simply cannot add more until it frees space or upgrades.

**Workflow outputs and agent-written files are not blocked** — the run was already paid for in credits, so
refusing the write would destroy paid work. They still count against the limit, which means a workspace
whose outputs have filled it must free space before the next upload or sync.

**Project history counts too.** Old file bytes and version documents kept for project history add to the workspace's used storage (see [project-versioning.md](project-versioning.md#quota-counting)). A restore can also be overwritten by the next sync: the local copy looks newer and is uploaded again (see [Residuals and limits](project-versioning.md#residuals-and-limits)).

## Seeing storage usage

`GET /api/v1/storage/usage` (any member of the workspace, unlike the billing summary, because a person who
hits the quota needs to see why) answers:

```json
{ "usedBytes": 4509715660, "limitBytes": 16106127360,
  "byProject": [{ "projectId": "…", "name": "Launch video", "usedBytes": 1234567890 }] }
```

`limitBytes` is `null` on an unlimited plan (Enterprise), and also when the deployment runs without billing.
`byProject` lists the **20** projects using the most storage, largest first. The route is
`StorageUsageController`, and it answers `404` when the caller has no active organization.

`GET /api/v1/billing/summary` gained a `storage` field with the same `{ usedBytes, limitBytes }`, so the
dashboard can show the bar without a second call. The dashboard shows a usage bar on **Billing** and in
**Settings** ("4.2 GB of 15 GB used"), a warning banner on project pages at **≥ 90 %**, and every
`STORAGE_QUOTA_EXCEEDED` (the upload tray, an assistant attachment, a sync error) as "Your workspace is out
of storage (X of Y used). Delete files or upgrade your plan." with a link to Billing.

## Uploads without a per-file size cap

There is **no per-file upload limit** below object storage's own ceiling. A large file goes straight to
object storage either as one presigned PUT or as an S3 multipart upload; the bytes never pass through the
Inference API, and the plan's storage quota is the real limit. The one implementation is
`IDirectUploadService` (`Services/Uploads/`), shared by the browser routes in `ProjectFilesController` and
the desktop app's folder sync, so both get the same ticket MAC, server-chosen key, HEAD verification and
quota checks.

**Single PUT or multipart.** A file of at most **256 MiB** (`DirectUploadLimits.SinglePutMaxBytes`) gets one
presigned PUT. A larger one gets an S3 multipart upload: the server creates it at ticket time; the client
asks for presigned part URLs in batches (at most **100** per call); it PUTs the parts, reading each part's
`ETag`; and completion sends the part list back.

**Part sizing.** `DirectUploadLimits.PartSizeFor` uses **64 MiB** (`MinPartBytes`) as the smallest part, or a
larger size when needed to stay within S3's **10,000** parts (`MaxParts`), rounded up to 1 MiB. The part
count is `ceil(size / partSize)`, and the last part is shorter.

**Ceilings and lifetimes.** The object ceiling is **5 TiB** (`DirectUploadLimits.MaxObjectBytes`), S3's own
maximum object size — not a product limit. A single-PUT ticket lives for the URL's 4 hours plus a 30-minute
grace (4 h 30 min); a multipart ticket lives **24 hours**, because a very large file can take longer than
4 hours to send, and each batch of part URLs is valid for at most 4 hours and never past the ticket.
Abandoned multipart uploads are removed by the bucket's lifecycle rule (**1 day** on R2); AWS and Garage
need the equivalent rule, otherwise the unfinished parts sit in the bucket.

**The browser routes** (paths unchanged, bodies extended):

| Route | Purpose |
|---|---|
| `POST /api/v1/projects/{projectId}/files/upload-url` | The ticket. It now carries `mode` (`singlePut` \| `multipart`), `uploadId`, `partSizeBytes`, `partCount` and `url` (single PUT only); `maxBytes` is 5 TiB |
| `POST /api/v1/projects/{projectId}/files/upload-parts` | `{ ticket echoed, partNumbers }` → part URLs (new) |
| `POST /api/v1/projects/{projectId}/files/upload-complete` | `{ ticket echoed, sizeBytes, parts? }`; `parts` is required for a multipart upload, each entry a part number and the `ETag` storage returned (new fields) |
| `POST /api/v1/projects/{projectId}/files/upload-abort` | `{ ticket echoed }` → `204` (new) |

**The form fallback is still bounded at 100 MB.** `POST /api/v1/projects/{projectId}/files` (the
multipart/form-data route, used by a self-host install until its bucket CORS is configured and by
non-browser clients) still travels through nginx and Cloudflare. nginx does **not** raise its cap for that
route: the server-level `client_max_body_size` (`CLIENT_MAX_BODY_SIZE`, default `100M`) applies, and
Cloudflare Free rejects a body over 100 MB, exactly as described in
[`direct-upload.md`](direct-upload.md). The direct and multipart-ticket routes exist to remove both limits
by taking the bytes out of that path.

**Refusals** carry a stable machine code and a plain sentence
(`DirectUploadRefusedException`): `STORAGE_QUOTA_EXCEEDED` (413, with used/limit),
`FILE_TOO_LARGE` (413, above 5 TiB), `INVALID_SIZE`/`INVALID_PATH` (400),
`UPLOAD_SIZE_MISMATCH`, `UPLOAD_OBJECT_MISSING`, `UPLOAD_INCOMPLETE` (409),
`UPLOAD_TICKET_EXPIRED` (410), `NOT_MULTIPART`/`INVALID_PART_NUMBERS` (400),
`REPLACED_FILE_NOT_FOUND`/`PROJECT_NOT_FOUND` (404), `DIRECT_UPLOAD_DISABLED` (501, this deployment cannot
presign or cannot do multipart).

## The data model

Both tables belong to the Inference API (migration `AddLocalFolderSync`, history table
`__EFMigrationsHistory_Api`). The WorkflowEngine maps the new column read-only with its own snapshot-only
migration `MapProjectFileSourceLink` (it creates nothing; `project_files` is excluded from engine
migrations).

| Table / column | Notes |
|---|---|
| `project_local_folder_links` | `id`, `project_id` (FK → `projects`, cascade), `organization_id`, `device_id` (FK → `runner_devices`, restrict), `target_folder` (≤ 512, unique per project), `display_path` (≤ 512, display only — the local path), `created_by_user_id` (the device's pairing user at link time, audit only), `paused_at`, `pause_reason` (`guard` \| `user`), `last_reconciled_at`, `last_change_at`, `file_count`, `last_error`, `created_at`, `updated_at`. Unique index `(project_id, target_folder)`. **No tenant query filter**: a child of `projects`, reached through the project access check (dashboard) or the signed device's own organization, and listed as a child table by `TenantBoundaryInventoryTests`. |
| `project_files.source_link_id` | uuid, nullable, FK → `project_local_folder_links` `ON DELETE SET NULL`, indexed. Unlinking nulls it, so the files survive as ordinary files. |

A synced row additionally carries `source_kind = 'local_folder'` and `source_revision` = the SHA-256 hex of
its content. A changed file keeps its row id; the service gives the replacement upload a **new object id**
so the new bytes land beside the old ones and the old object is deleted only after the new upload is
verified. A refused or abandoned replacement therefore never destroys the file it was meant to replace.

## The desktop app on the machine

The engine is `runner/internal/foldersync/`; the signed HTTP client is `runner/internal/deviceapi/`. What
they do:

- **Watch.** Per link, `fsnotify` adds a watch per directory (recursive, by walking; new directories are
  added as they appear). Events are debounced for **2 s** of quiet and coalesced per path, and a file is
  uploaded only after its size and mtime have been stable for **2 s**, so a file still being written is
  never uploaded half-done.
- **Catch up.** A full scan and manifest run at start, after a wake or a reconnect, and every **15 minutes**
  as a safety net. A local index at `os.UserCacheDir()/ReelBolt/folder-index/<linkId>.json`
  (path → size, mtime, SHA-256, last accepted SHA-256) means an unchanged file is never hashed twice. If a
  folder would need more than **20,000** directory watches, the engine falls back to a rescan every
  **2 minutes** and says so in its status.
- **Upload.** Through the ticket: one PUT, or parts PUT **3 at a time**, each part retried up to **3** times
  with backoff, and re-presigned when its URL expires. On failure the upload is aborted and retried later.
  Uploads are not bandwidth-throttled.
- **Live changes** go straight to `uploads`/`delete`; only the catch-up and safety passes use the manifest.
- **Requests** are signed as described above, against the API origin recorded at pairing. A `401` stops
  syncing and shows "This computer is no longer connected to ReelBolt — pair it again";
  `403 device_owner_lacks_access` shows a plain message; `413` pauses that file with "Storage is full
  (X of Y used)".

**Local files.** Links live in `os.UserConfigDir()/ReelBolt/folders.json` (0600): `linkId`, `localPath`,
`projectId`, `projectName`, `targetFolder`, `paused`, `confirmLargeDelete`. A running engine publishes its
status to `folder-sync.status.json` in the runner config directory, which is what `folders list`, the tray
and the window read — so a headless daemon and the window can never disagree. The local path is never sent
to the server except as the link's `displayPath` string, and the status document is local only.

**Command line** (`reelbolt-runner`, which shares `folders.json` with the window):

```sh
reelbolt-runner folders list [--json]          # every link, with live status when a runner is publishing it
reelbolt-runner folders add <path> --project <id> [--target <name>] [--api <origin>]
reelbolt-runner folders remove <linkId>        # the files already copied stay in the project
reelbolt-runner folders pause <linkId>         # a local pause; nothing is copied until it is resumed
reelbolt-runner folders resume <linkId> [--confirm-large-delete]
reelbolt-runner config set-api <url>           # (or --clear) set the origin folder sync signs for
reelbolt-runner config show
```

`folders add` creates the link on the server (the server assigns the id and validates the target folder
name), then records it locally. `folders remove` tells the server first, then forgets it locally even if the
machine is offline. `run` starts the sync engine beside the job loop, so a headless machine syncs without a
window. A machine paired before folder sync existed has no API origin: the commands answer "Pair this
computer again to turn on folder sync", and `config set-api` is the escape hatch.

**Desktop window** (`runner/internal/ui/` + `runner/frontend/`, Wails host in
`cmd/reelbolt-runner-desktop`). A **Folders** section lists each synced folder with its local path, the
project and target folder, and a status of **Up to date**, **Syncing N files**, **Paused**, or **Needs
attention** with the reason. It offers **Add a folder** (native folder picker → project → target folder
name, prefilled with the local folder's name), **Pause**, **Resume** (shown as **Resume and remove deleted
files** when the mass-delete guard paused the link, because that resume confirms the deletions), **Open
project in browser**, and **Remove** (confirmed with "the files already copied stay in the project"). The
tray shows a one-line sync status, and the screen keeps no state of its own: it reads the engine's status
document, so it cannot invent a state the engine does not have. **Windows and Linux only** (D26); iCloud is
covered on Windows through Apple's iCloud for Windows, which exposes iCloud Drive as a folder.

## Troubleshooting computer folder sync

These are the plain-language errors, what causes each, and the fix. The app shows the sentence; the
machine code is for the log and for an operator reading this table.

| What it says | Cause and fix |
|---|---|
| "This computer is no longer connected to ReelBolt — pair it again" (`401 device_unauthorized`) | The device is unknown, **revoked**, not a desktop device, has no organization or pairing user, its clock is more than 120 s off, or a signature did not verify. Pair the computer again. The server never says which. |
| "The person who connected this computer can no longer change projects in this workspace. Connect the computer again with an account that can." (`403 device_owner_lacks_access`) | The pairing user left the workspace or lost edit rights. Re-pair with an account that can edit. |
| "This folder is no longer linked to a ReelBolt project." (`404 link_not_found`) | The link was removed (from the dashboard or another session). Re-add the folder. |
| "Syncing this folder is paused. Resume it to sync again." (`409 link_paused`) | A person paused the link, or the mass-delete guard did. Resume from the app (guard) or the dashboard (either). Aborting an upload is still allowed while paused. |
| "The folder on `<device name>` looks empty or mostly gone — sync is paused so nothing is deleted. If you really removed those files, resume it from the desktop app." | The mass-delete guard. Check the folder is really mounted and hydrated; if the deletions were intended, Resume from the app and confirm them. |
| "The project already has this version of the file." (`409 unchanged`) | Nothing to do: the content is already in the project, so no upload happens. |
| "A file from another source already has this name in the project, so this one is skipped." (`409 occupied`) | An upload, an agent file, GitHub or a cloud drive holds that path. Rename or move the file in the project, or give the local file a different name. |
| "Storage is full (X of Y used)" (`413 STORAGE_QUOTA_EXCEEDED`) | The workspace is out of storage. Free space (delete files) or upgrade the plan, then sync again. Nothing was deleted. |
| "That file's path cannot be synced…" (`400 invalid_path`); "The file's SHA-256 must be 64 hexadecimal characters." (`400 invalid_sha256`); "The file's size cannot be negative." (`400 invalid_size`) | The computer sent a path or hash the project's rules cannot hold. Usually a very long or unusual file name; rename it, or leave it out of the folder. |
| "That upload is not valid for this folder. Start the upload again." (`400 invalid_ticket`); `INVALID_PART_NUMBERS` | The echoed ticket did not verify, belonged to another caller, link or project, or a part number was out of range, duplicated or over 100 in one call. Restart the upload. |
| "Some parts of the folder's list did not arrive. Send the list again." (`409 manifest_incomplete`) | Batches were missing, arrived after the 15-minute lifetime, or landed on a different replica of a multi-replica deployment. The app starts a new manifest. |
| "A synced folder can hold at most 200,000 files. Choose a smaller folder." (`413 manifest_too_large`) | The folder is over the manifest ceiling. Sync a sub-folder instead. |
| "A manifest batch needs a manifest id, a batch number from 0, and at most 2,000 files." (`400 invalid_manifest`) | A malformed batch. The app sends the manifest again. |
| "A request to this endpoint is at most 4 MiB." (`413 body_too_large`) | The signed body was over 4 MiB. Manifests are batched, so this is a client bug; the file bytes never travel here. |
| "That folder name is already used in the project…" (`409 target_folder_taken`) / "That folder name cannot be used in a project." (`400 invalid_target_folder`) | Two links may not share, contain or sit inside each other's target folder. Pick another `--target` (or another name in the app). |
| Files are missing from the project | They are hidden, system clutter (`~$…`, `*.tmp`, `*.part`, `*.crdownload`), symlinks, they are **only in the cloud and not downloaded** (use "Always keep on this device"), their name is not a valid project path, another source holds the path, or they are over the workspace's storage limit. The app's status names the reason and shows examples. |
| Nothing syncs at all; `config show` says folder sync is off | The machine has no API origin (paired before folder sync existed) or is not paired. `reelbolt-runner config set-api <url>`, or pair again. |
| The folder is watched but changes take minutes | More than 20,000 directories: the engine fell back to a rescan every 2 minutes and says so in its status. Sync a smaller folder for live updates. |

Operator-side: a `401` storm in the log with the reason field is the only place the true cause is visible
(`DeviceAuthFailure`). A revoked device is answered like an unknown one by design.

## What computer folder sync deliberately does not do

- **No two-way sync.** Nothing is ever written, moved, renamed or deleted on the computer; the folder is
  the source of truth for the files the link wrote.
- **No sync while the app is closed.** Impossible by construction: the engine runs in the desktop app (or
  the headless `run` process). The dashboard says when a computer is offline (`deviceOnline` false).
- **No native iCloud API.** None exists for a server, which is why the feature is a folder sync at all.
- **No macOS desktop app for now** (D26). Windows and Linux only.
- **No bandwidth limits**, and folder sync is not gated by the job governor: it copies whenever the app runs.
- **No push notifications.** A change made while the app was off is caught up by the next start, wake,
  reconnect or 15-minute pass.
- **Synced files are not summarized or embedded** automatically, as with GitHub and the cloud drives.
- **No per-file upload cap**, but the plan's storage quota is the real ceiling, and nothing is ever deleted
  automatically to make room.
