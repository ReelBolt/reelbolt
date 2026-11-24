# Project versioning

Project versioning keeps a history of a project and lets a person compare a saved version with the
project as it is now, restore one item or the whole project, and erase a file's old bytes for good.

The behaviour lives in `inference/src/ReelBolt.Inference.Api/Services/History/`, the decorator in
`Services/Storage/ProjectHistoryProtectingStorage.cs`, the routes in
`Controllers/ProjectHistoryController.cs`, and the assistant tools in `Agents/Tools/AssistantHistoryTools.cs`.
Scope: this feature is history, diff and restore; merging between project copies is a later
project. Automatic versions expire, named versions do not, history bytes count against storage, and
"Delete forever" removes them. Item restore needs project Edit; whole-project restore needs project
Delete.

## What a version holds and what it never holds

A version is an immutable manifest of one project at one moment. Each entry is a `ProjectVersionItem`
with a kind, an item key, a display name and, for the kinds that have a body, an identity of that body.
The six kinds are:

| Kind | Item key | What is stored |
|---|---|---|
| `File` | the project file id | path, category (`userFiles` or `agentFiles`), MIME type, size, `ContentId`, storage key and the storage ETag read at capture |
| `Workflow` | the workflow id | the canonical JSON document of a Standard workflow, steps included |
| `Timeline` | the timeline id | the revision number and the canonical document JSON |
| `DesignSystem` | `design-system` | the project's design-system `CurrentVersionId` and that version's document digest; the design-system version row is the body, so no copy is made |
| `Pronunciation` | `pronunciation` | the canonical JSON of the whole pronunciation entry set, sorted by term |
| `Project` | `project` | the canonical JSON `{name, description}` |

A version never captures outputs (`outputFiles` rows), workflow runs or their results, usage or
credits, voices (consent must never roll back), summaries, embeddings or derivatives, link rows
(folder, cloud drive, GitHub), hidden workflow kinds, or a file row whose moderation status is
quarantined or whose CSAM status is quarantined.

Only live Standard workflows are captured; hidden composition or system workflows are not. A
soft-deleted timeline is not captured because the timeline query filter excludes it. Files outside
`userFiles` and `agentFiles` are not captured.

The version row itself (`project_versions`) carries a per-project number starting at 1, an optional
name (1 to 80 characters after trimming, no control or Unicode format characters), a cause, the user
who asked for it, `CreatedAt`, `ExpiresAt`, a manifest digest, the item count and the sum of file
sizes. The manifest digest is the SHA-256 of the canonical JSON of the sorted item list; document
bodies are deduplicated in `project_history_documents` by the SHA-256 of their canonical JSON, and
protected file bytes are deduplicated in `project_history_blobs` by content identity.

## Copy-on-change and the decorator choke point

History must hold the bytes a version named even after the live file is overwritten or deleted. One
decorator, `ProjectHistoryProtectingStorage`, sits over the API's `IFileStorageService` and is the only
place that copies old bytes. Every API consumer of the storage seam goes through it; the raw
`S3FileStorageService` is not exposed as `IFileStorageService`.

Before an `UploadAsync` to an existing key, a `DeleteAsync` or `DeleteManyAsync`, a `MoveAsync`, a
`MoveIfMatchAsync`, or a `CopyAsync` whose destination is a live file key, the decorator looks for
non-erased items of non-expired versions with that project and storage key. Keys outside
`projects/{projectId}/userFiles/` and `projects/{projectId}/agentFiles/` (outputs, history,
quarantine, editor scratch) pass straight through. A key no live version names costs nothing and is
not copied.

For each distinct content identity that has no blob row yet, the decorator HEADs the live object,
compares its ETag with the ETag the item captured, and only then copies the object server-side to
`projects/{projectId}/history/blobs/{sha256-of-content-id}` and inserts the blob row with the size
from the HEAD. If the ETag differs or the item has no verified ETag, the bytes are already gone: the
decorator logs a warning and writes nothing, and the item is later reported as not restorable. A
concurrent insert of the same `(project, content id)` is treated as success.

A protection failure fails the caller's operation. If the HEAD, the copy or the blob row cannot be
written, the exception propagates and the delete, move or overwrite never happens. Quota is
deliberately not checked here: a delete must not be blocked by a full plan. After an overwrite, move
or copy that changes bytes at a key, the decorator clears `content_sha256` and `content_sha256_etag`
on the rows that name the affected keys.

Quarantine moves must not retain the bytes they are removing. `ProjectHistoryProtection.Suppress()`
is an async-local scope that disables protection for the current flow; the CSAM quarantine service
and the moderation processor's quarantine path move the object inside it and then call the eraser
(see "Delete forever and quarantine"). The scope composes, so a nested `Suppress()` restores the
previous value when disposed.

The engine never overwrites or deletes a live project file key; it writes new rows and keys, so the
decorator does not fire for engine work. An inventory test,
`ProjectHistoryWritePathInventoryTests`, fails if a class in `ReelBolt.Inference.Api` takes a direct
`IAmazonS3` dependency outside an allowlisted set (purge, sampler, presigner, copier and similar),
with a reason for each entry.

## ContentId: how a file item names its bytes

`ContentId` is a string of at most 80 characters. It is built by `ReelBolt.Shared/History/ContentId.cs`
and records how sure the capture was that it names the bytes:

- `sha256:<64 lowercase hex>` when the file row already knows `content_sha256` and the
  `content_sha256_etag` that hash was taken at. The sweeper below fills those columns in the
  background, so a hashed file costs no HEAD at capture.
- `etag:<64 hex>` when the row has no verified hash. The hex is the SHA-256 of `"{etag}|{sizeBytes}"`
  from a HEAD at capture. The store's ETag is opaque, so this is an identity, not a content hash.
- `unverified:<fileId>` when the HEAD was skipped or failed. Capture HEADs at most
  `ProjectHistory:MaxHeadsPerCapture` unhashed files, eight at a time; files past the cap and files
  whose HEAD failed are stored with no ETag and this marker.

An `unverified` item can be restored only while the live file still exists and its ETag still matches
the captured one; an item with a blob row restores from the blob. A `sha256`/`etag` item with no blob
can also restore from the live object after the live ETag is checked.

## The hash sweeper

`ProjectFileHashSweeper` is a background service that fills `project_files.content_sha256` and
`content_sha256_etag` without ever working inside a request. It runs every
`ProjectHistory:HashSweepIntervalSeconds` (60) when `ProjectHistory:HashSweepEnabled` is true (default),
takes `ProjectHistory:HashSweepBatchSize` (50) rows at a time from `userFiles`/`agentFiles` that are
not quarantined and have no hash, oldest first, and for each row:

1. reads the object's ETag (A);
2. streams the object and computes its SHA-256;
3. reads the ETag again (B).

It saves both columns only when A and B are equal. A file overwritten during the read is skipped and
retried on a later sweep. A row whose object is missing or unreadable is remembered for the life of the
process, so it cannot become the "oldest unhashed" row forever and starve the rest.

## Retention

`ProjectHistory:AutoRetentionDays` (30) sets `ExpiresAt` for every cause except `named`: `named`
versions have a null `ExpiresAt` and are kept until deleted. Renaming a version to a blank or null
name makes it automatic again, keeps its cause and sets `ExpiresAt` to now plus the retention window.
The list and detail reads ignore expired versions even before the garbage collector removes them.

Two per-project caps apply at capture:

- at most `ProjectHistory:MaxNonNamedVersions` (200) automatic versions; the oldest automatic versions
  are pruned at capture to make room;
- at most `ProjectHistory:MaxNamedVersions` (100) named versions; a new name over the cap is refused
  with `409 NAMED_VERSION_LIMIT`.

`ProjectHistoryRetentionService` runs hourly (`GcBatchSize`, 500, per step). It deletes expired
versions, then deletes the blob objects and rows no remaining non-erased item references, then the
document rows no item references. Blob objects are deleted through the storage seam's
`DeleteManyAsync`, which the decorator passes through for `history/` keys. A failed object delete keeps
the row as the map back to the surviving object, and the next pass retries.

Deleting a version removes its rows; its bytes are freed by the next GC pass, not synchronously.
Deleting a project cascades its version, item, blob and document rows. The project delete route also
calls `IProjectHistoryEraser.DeleteProjectObjectsAsync`, which removes the objects under the
project's `history/` prefix, because the rows would otherwise disappear before the objects.

## Quota counting

History bytes count against the workspace's storage quota.
`StorageUsageService.GetUsedBytesAsync` adds, for the organization's projects, the sum of
`project_history_blobs.size_bytes` and the sum of `project_history_documents.size_bytes` to the
project files, picture working copies and brand-library assets it already counted. The quota, the
billing summary and `GET /api/v1/storage/usage` therefore see the same figure.

The version list route returns `historyBytes` for the project, computed as the sum of its blob and
document bytes. A version's `totalBytes` is the sum of its file sizes and is informational; it is not
the retained storage, because identical bytes may be shared by several versions and documents add
their own bytes.

History is never a reason to refuse a delete or an overwrite: the decorator does not check quota. A
restore that adds bytes to live files is checked on the net growth per file item, and a file that
would grow past the plan is skipped with `STORAGE_QUOTA_EXCEEDED` while the rest of the restore
proceeds.

## "Delete forever" and quarantine

`DELETE /api/v1/projects/{projectId}/history/files/{fileId}` needs project Delete. It calls
`IProjectHistoryEraser.EraseFileAsync`, which sets `ErasedAt` on every File item of that project whose
key is the file id, collects their content identities, marks every other File item in the project that
shares one of those identities, saves the marks, then deletes the protected objects and blob rows for
those identities. The operation is idempotent. An erased item is never restorable and reads show
`ERASED`. If some objects cannot be deleted the eraser throws and keeps the rows, so a later GC pass can
retry; the items are already marked, so no restore can use the bytes in the meantime.

Quarantine is the same operation without a user click. `CsamQuarantineService` and
`ContentModerationProcessor.QuarantineAsync` move the quarantined object inside a
`ProjectHistoryProtection.Suppress()` scope, so copy-on-change never copies it into history, and then
call `EraseFileAsync`. Quarantined bytes are therefore neither retained nor restorable.

Every path that serves a file's bytes from history uses `ProjectHistoryContentGuard`: item diff, item
content download, restore source and the per-item restorability flag. The guard refuses an item whose
version was captured at or before the project's `ContentQuarantinedAt` or `CsamOutputHoldAt` (plus the
shared clock-skew margin) with `QUARANTINE_MARKER`, and refuses a live file the platform's output hold
holds with `OUTPUT_HELD`. A live row already marked quarantined is refused with `QUARANTINED`. A failed
hold read holds everything.

## Auto triggers

All automatic capture is best effort: `TryCaptureAutoAsync` owns a
`ProjectHistory:AutoCaptureTimeoutSeconds` (10 s) timeout, catches and logs every failure, and never
fails or blocks the action it protects. `ProjectHistory:AutoCapture` (true) is the master switch for
all three triggers.

- **Before a run (`beforeRun`).** `RunStartGate` calls the capture after the run's gates have succeeded
  and before the caller publishes, at the one point Execute, Retry, Resume and the workflow-assistant
  execute all pass.
- **Before an assistant or MCP write (`beforeAssistant`).** `AssistantToolInterceptor` captures before
  an in-app Apply tool named in `ProjectHistoryAutoCapture.AssistantWriteTools`, when a project is in
  scope. The MCP bridge resolves the same cause for the collapsed call and captures once before it.
  The set covers file write/edit/insert/delete/upload/import/save-output-as-file, workflow
  save/patch/clone/create-from-template, pronunciation save, picture document create, green-screen
  insert and media overlay. A drift test keeps the set complete against the registered Apply tools;
  `AssistantApplyToolsLeftOut` records every Apply tool deliberately left out and why.
- **Idle (`idle`).** `ProjectHistoryIdleCaptureService` runs every
  `ProjectHistory:IdleSweepIntervalSeconds` (600). A project is due when its newest change (maximum of
  file `UploadedAt`, workflow and timeline `UpdatedAt`, project `UpdatedAt`) is newer than its newest
  version, is at least `ProjectHistory:IdleMinutes` (10) old, and falls inside
  `ProjectHistory:IdleLookbackHours` (24). At most `ProjectHistory:IdleSweepBatchSize` (200) projects
  are captured per pass. The capture has no user.

Capture deduplicates: for every cause except `named` and `beforeRestore`, if the new manifest digest
equals the newest version's digest, the service returns the newest version and writes no row. A
capture is serialized per project with `pg_advisory_xact_lock(hashtext('project-history:' || p))` on
PostgreSQL, so two replicas cannot allocate the same number; on a provider without advisory locks
(the in-memory tests) the lock statement is skipped. A project with more than
`ProjectHistory:MaxFilesPerVersion` (20000) live files is refused with
`413 PROJECT_TOO_LARGE_TO_VERSION`.

## Restore rules

The restore routes validate the body before any permission decision, then make exactly one
`IProjectAccessService` call. Item scope needs project Edit; whole-project scope needs project Delete
A missing or foreign project and a version id belonging to another project are 404; a role
too low is 403.

Before any kind handler runs, the restore service applies these guards in order:

1. the version must exist and not be expired;
2. project scope refuses with `409 PROJECT_RUN_ACTIVE` when any execution of the project is Queued or
   Running; item scope refuses with `409 WORKFLOW_RUN_ACTIVE` when a workflow being restored has such a
   run;
3. the project's restore lock (a session-level `pg_try_advisory_lock` on a dedicated unpooled
   connection; an in-process lock when the database is not PostgreSQL) refuses with
   `409 RESTORE_IN_PROGRESS` if another restore holds it;
4. a safety version is captured with cause `beforeRestore`, which is never deduplicated, so the restore
   can itself be undone.

Then the per-kind handlers apply the version. A kind with no registered handler is skipped
`NOT_YET_SUPPORTED`; a partial success is normal.

**Files.** Bytes come from the protected blob, or from the live object when its ETag still matches the
captured one. A deleted file is recreated with its old id at its old path; if another live file holds
that path the item is skipped `PATH_TAKEN` (project scope removes the version's absent files first, so
in that scope a collision is real). Bytes are written through the decorated storage, so the current
bytes are protected for the safety version. Quota is checked only on the file's net growth; a
shrinking or equal rewrite always passes, and growth past the plan skips just that item with
`STORAGE_QUOTA_EXCEEDED`. The row's moderation status is reset to unmoderated, its CSAM status to
pending, its summary and indexing to pending for text, and the same post-write work as a direct upload
runs (scan, moderation, indexing, summary or a media derivative, activity). A live quarantined row is
skipped `QUARANTINED`; the content guard can also skip `QUARANTINE_MARKER` or `OUTPUT_HELD`.

**Workflows.** The stored canonical document is turned back into save requests and rebuilt through the
same rules every editor save uses: `WorkflowEditDiffService.ValidateProposedSteps`,
`StepProviderOwnershipValidator`, `StepAgentAuthorization` and `BuildStep`. A workflow that no longer
passes is skipped whole with `WORKFLOW_INVALID`; nothing is written. A workflow with a Queued/Running
run is skipped `WORKFLOW_RUN_ACTIVE`. A workflow deleted since the capture is recreated at its old id
when that id is free, otherwise at a new id; an id held by another project or a hidden row is never
touched. `allowSourceMediaEgress` is a customer consent, never an artefact of a version: a captured
step keeps it only where the live workflow has the same step (same order and type) with that consent
already granted; everywhere else it is written false and the workflow reports the notice
`EGRESS_CONSENT_RESET`, so the customer re-grants it in the app.

**Timelines.** The stored document is validated by `EditTimelineValidator` against the project as it
is now and committed through the same `EditTimelineCommit` core as the editor's restore, keeping the
current revision as a `beforeRestore` snapshot and adding a restore marker to the op log. A document
the validator refuses is skipped `TIMELINE_INVALID`. A soft-deleted timeline is undeleted and restored
as the next revision; a hard-deleted one is recreated with its stored name and document at revision 1.

**Design system.** `IDesignSystemService.RestoreAsync` is called with the pinned version id. A version
row that no longer exists is skipped `DESIGN_VERSION_GONE`; a version already current is skipped
`ALREADY_CURRENT`; a version the service cannot restore is skipped `DESIGN_VERSION_INVALID`.

**Pronunciation.** The captured set replaces the live set in one save, so a partially applied glossary
is never observable. Invalid entries are dropped and the per-project entry cap applies.

**Project details.** The captured name and description are written to the project.

In whole-project scope the service also removes what the version does not have: files through the
normal delete path (so their bytes stay in the safety version), Standard workflows through
`WorkflowStepReplacer.DeleteAsync`, and timelines by soft delete. It never touches runs, outputs,
voices or links. The result lists `restored`, `removed`, `skipped` (each with a reason code) and
`notices`, plus the safety version's id.

## Diff

`GET versions/{versionId}/changes?to={versionId|current}` compares two manifests by `(kind, key)`. Each
entry names the item and says `added`, `removed`, `changed`, `moved` or `changedAndMoved`. A file's
content change is decided from its content identity, with the live row's verified hash consulted when
one side is an ETag-derived or unverified identity; a change of display name is a move. The list is
sorted by kind and key and truncated at 5,000 changes with `truncated: true`; `unchangedCount` says how
many matched items did not change.

The item diff returns a mode and the body for that mode: `text` with hunks from the first-party Myers
diff (`LineDiff`, three context lines, line text capped at 2,000 characters) for a text file up to
1 MiB and 20,000 lines per side; `tooLarge` or `binary` with the two sizes otherwise; `workflow` with
added, removed and changed steps by order, type and agent name plus the changed config field names;
`timeline` with the revisions and track, clip and duration summaries; `designSystem` with
`DesignDocumentDiffer.Diff`; `pronunciation` with added, removed and changed terms; `project` with only
the changed name and description fields. `unavailable` means the content guard refuses the bytes
(CSAM output hold or quarantine marker), so no content is included.

## REST

All routes are under `api/v1/projects/{projectId:guid}/history` and are in
`ProjectHistoryController`, which is listed in `ReviewedProjectRouteControllers`. Every action makes
one `IProjectAccessService.GetAsync` call.

| Method and path | Permission | Result |
|---|---|---|
| `GET versions?limit=50&before={number}` | View | `{versions, historyBytes, retentionDays, canEdit, canDelete}` newest first; limit is clamped to 1..200 and `before` is a version number |
| `POST versions` with `{name}` | Edit | `201` the new named version; `400 INVALID_NAME`; `413 PROJECT_TOO_LARGE_TO_VERSION`; `409 NAMED_VERSION_LIMIT` |
| `GET versions/{versionId}` | View | the version and its `items` with `restorable` and an optional `notRestorableReason` |
| `PUT versions/{versionId}/name` with `{name}` | Edit | the version; a null or blank name makes it automatic again; `409 NAMED_VERSION_LIMIT` |
| `DELETE versions/{versionId}` | Delete | `204`; the GC frees the bytes later |
| `DELETE files/{fileId}` | Delete | `204`; "Delete forever" erases that file's content from every version |
| `GET versions/{versionId}/changes?to={versionId|current}` | View | the manifest diff; default target is the live project |
| `GET versions/{versionId}/items/{kind}/{key}/diff?to=...` | View | one item's diff |
| `POST versions/{versionId}/restore/preview` | Edit (item) or Delete (project) | the changes, blocked items and estimated bytes; writes nothing |
| `POST versions/{versionId}/restore` | Edit (item) or Delete (project) | the restore result with the safety version id |
| `GET versions/{versionId}/items/{kind}/{key}/content` | View | a file item's bytes at that version, under the safe-content headers |

The restore body is `{scope: "item"|"project", items?: [{kind, key}]}`. Item scope needs 1 to 50
items; a missing or unknown scope is `400 INVALID_SCOPE`, a bad item is `400 INVALID_ITEM_KIND` or
`400 INVALID_ITEMS`, and a bad diff target is `400 INVALID_TARGET`. Restore conflicts are `409` with
`PROJECT_RUN_ACTIVE`, `WORKFLOW_RUN_ACTIVE` or `RESTORE_IN_PROGRESS`. Responses never carry storage
keys, ETags, content identities, digests or bucket names.

The per-item reason codes are `CONTENT_GONE`, `ERASED`, `QUARANTINED`, `QUARANTINE_MARKER`,
`OUTPUT_HELD`, `DESIGN_VERSION_GONE`, `DESIGN_VERSION_INVALID`, `UNVERIFIED`, `NOT_YET_SUPPORTED`,
`PATH_TAKEN`, `STORAGE_QUOTA_EXCEEDED`, `ITEM_NOT_FOUND`, `ALREADY_CURRENT`, `WORKFLOW_INVALID`,
`WORKFLOW_RUN_ACTIVE` and `TIMELINE_INVALID`.

## Configuration keys

All keys bind from the `ProjectHistory` section (`ProjectHistoryOptions`). The shipped defaults are in
`inference/src/ReelBolt.Inference.Api/appsettings.json`.

| Key | Default | Meaning |
|---|---|---|
| `MaxFilesPerVersion` | `20000` | Refuse to version a project with more live files; `413 PROJECT_TOO_LARGE_TO_VERSION` |
| `AutoRetentionDays` | `30` | Days an automatic version is kept; named versions never expire |
| `MaxHeadsPerCapture` | `2000` | HEADs one capture may issue; the rest are stored `unverified` |
| `HeadConcurrency` | `8` | HEADs one capture runs at once |
| `MaxNonNamedVersions` | `200` | Automatic versions per project; the oldest is pruned at capture |
| `MaxNamedVersions` | `100` | Named versions per project; a new one over the cap is `409 NAMED_VERSION_LIMIT` |
| `AutoCapture` | `true` | Master switch for every automatic capture trigger |
| `AutoCaptureTimeoutSeconds` | `10` | How long one automatic capture may run before it is abandoned |
| `IdleSweepIntervalSeconds` | `600` | How often the idle sweep looks for changed projects |
| `IdleSweepBatchSize` | `200` | Projects the idle sweep may capture in one pass |
| `IdleMinutes` | `10` | A project is idle once it has had no change for this long |
| `IdleLookbackHours` | `24` | Only projects changed within this window are considered |
| `HashSweepEnabled` | `true` | Master switch for the background content-hash sweeper |
| `HashSweepIntervalSeconds` | `60` | Seconds between hash sweeps |
| `HashSweepBatchSize` | `50` | Files hashed per sweep, oldest first |
| `GcBatchSize` | `500` | Rows the retention GC removes per step per pass |

## Residuals and limits

- **Folder sync can overwrite a restored file.** A local folder link keeps its own local copy. After a
  restore, the next sync sees the local file as newer and uploads it, replacing the restored bytes. To
  keep a restored version on a synced path, restore or edit the file on the computer too, or pause the
  link around the restore. See [`local-folder-sync.md`](local-folder-sync.md).
- **Unverified items are only conditionally restorable.** A file stored `unverified` (past the HEAD
  cap or after a failed HEAD) has no captured identity, so it can be restored only while the live file
  still exists and is unchanged. Copy-on-change also cannot protect its old bytes, so once the live
  bytes change the version cannot supply them.
- **Restore is bounded by the safety version.** A restore always captures a `beforeRestore` safety
  version first, so it is subject to `MaxFilesPerVersion` as well; a project above that limit cannot be
  restored.
- **Deleted versions free bytes later.** Deleting a version removes its rows immediately, but the
  blob objects and document rows are freed by the hourly GC, not in the request. The reported usage can
  lag one GC pass.
- **The design-system body is not copied.** A version pins the design-system version id; if that row is
  later pruned by design-system retention, the item reports `DESIGN_VERSION_GONE` and nothing else in
  the version can recreate the document.
- **Merge is a separate initiative.** This feature compares, restores and erases; merging two projects
  belongs to the later project-merge initiative.
