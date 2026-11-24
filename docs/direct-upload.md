# Direct uploads to object storage

A large upload from a browser does not travel through the Inference API. The browser asks the API for
permission and a presigned URL, PUTs the bytes straight to the object store, and then tells the API it
is done — so neither Cloudflare nor nginx ever sees the bytes, and the file is recorded only after the
API has HEADed what actually arrived.

The alternative was rejected in `tls.md` "Upload size": an unproxied `upload.<domain>`
hostname would expose the origin IP, bypass the WAF for that name, and need a second host on the auth
cookie plus its own CORS. Direct-to-storage is the option that page recommends, and this is its
implementation.

## Why the API cannot be in the path

Cloudflare Free rejects a request body over 100 MB and gives the origin 100 seconds to respond; nginx
carries `client_max_body_size 100M` (`CLIENT_MAX_BODY_SIZE`, its default). The API accepts a direct upload
of up to 5 TiB — object storage's own maximum object size — and **no nginx route raises its body cap for
this path**: the form fallback below stays bounded by that same 100 MB at both proxies, which is exactly why
the bytes must not travel through them. A phone on a slow uplink
takes minutes to send a video, Cloudflare gives up at 100 seconds, and the browser has already sent the
bytes — the upload looks finished and then fails. Removing the bytes from that path removes both limits
at once, and takes the upload load off the control VM.

## The three steps (a single PUT)

A file of at most **256 MiB** (`DirectUploadLimits.SinglePutMaxBytes`) gets one presigned PUT. A larger file
gets a multipart ticket instead — see "Large files go up in parts" below.

| Step | Request | Answer |
|---|---|---|
| 1. Ask | `POST /api/v1/projects/{projectId}/files/upload-url` with `{ fileName, sizeBytes, contentType?, relativePath? }` | `{ uploadToken, key, fileId, url, expiresAtUnix, contentType, maxBytes, mode: "singlePut" }` |
| 2. PUT | `PUT <url>`, the file as the body, `Content-Type: <contentType from the ticket>` | storage's own 200, or its own 403 `SignatureDoesNotMatch` |
| 3. Confirm | `POST /api/v1/projects/{projectId}/files/upload-complete` with the ticket echoed back plus `sizeBytes` | `201` and the recorded file, exactly as the multipart route returns it |

**The key is the server's, never the client's.** Step 1 generates the file id and derives the key from
it — `projects/{projectId}/userFiles/{folderPath}/{fileId}{extension}`, the same derivation
`S3FileStorageService.UploadAsync` uses, so a direct upload and a multipart upload of the same
file land on identically shaped keys. The client is told the key; it can never propose one. A
client-chosen key would be an arbitrary-write primitive against another tenant's prefix.

**The ticket is an HMAC, not an opaque id.** `uploadToken` is
`IConfirmationTokenService.Compute("BrowserUpload", …)` over a length-prefixed frame of the
project, file id, key, file name, path, content type, claimed size and expiry — bound to the caller's
user id and the project's organization. Finalisation rebuilds that frame from the request body and
recomputes the MAC in fixed time, so every field a completion presents is provably one this server
issued, to this caller, for this project. Nothing is stored server-side between the two calls, so there
is no row to expire, sweep or race on.

**The PUT URL is a bearer capability.** It is returned to the browser and never logged, and the client
sends no cookies to storage: the signed URL is the whole authorization.

## Large files go up in parts

There is **no per-file upload cap** below object storage's own ceiling of **5 TiB**
(`DirectUploadLimits.MaxObjectBytes`), which is the most a single S3/R2 object can hold. A file above
`SinglePutMaxBytes` (256 MiB) is uploaded as an S3 multipart upload, so a multi-gigabyte source video no
longer fails at the 20 GB the old single-PUT ticket allowed. The plan's storage quota is the real limit.

Five calls, all on the same controller and the same project access check:

| Step | Request | Answer |
|---|---|---|
| 1. Open | `POST …/files/upload-url` | A ticket with `mode: "multipart"`, `uploadId`, `partSizeBytes` and `partCount`; `url` is null. The server created the multipart upload already |
| 2. Part URLs | `POST …/files/upload-parts` with the ticket echoed plus `{ partNumbers }` | `[{ partNumber, url, expiresAtUnix }]` — at most **100** part numbers per call, so a client asks in batches as it goes and no URL outlives its part by hours |
| 3. Upload | `PUT <part url>`, the part's bytes as the body | storage's own 200 and an **`ETag`** response header, which the client must keep |
| 4. Complete | `POST …/files/upload-complete` with the ticket echoed, `sizeBytes` and `parts: [{ partNumber, etag }]` | `201` and the recorded file. Completion is a server-side `CompleteMultipartUpload`, then the same HEAD and verification as a single PUT |
| 5. Abort | `POST …/files/upload-abort` with the ticket echoed | `204`. Aborts the multipart upload and frees its parts; best effort and idempotent |

**Part sizing.** `DirectUploadLimits.PartSizeFor` picks **64 MiB** (`MinPartBytes`) as the smallest part, or a
larger size when that is needed to stay within S3's **10,000** part limit (`MaxParts`), rounded up to 1 MiB.
`partCount` is `ceil(size / partSize)`, and the last part is shorter than the rest. Completion must list
every part, numbered from 1 with no gaps, each with the `ETag` storage returned for it; a missing part or a
wrong `ETag` is `409 UPLOAD_INCOMPLETE`.

**Lifetimes.** A multipart ticket lives **24 hours** (`DirectUploadService.MultipartTicketTtl`), because a
very large file can take longer than a single PUT's four-hour window to send. Each batch of part URLs is
valid for at most **4 hours** and never past the ticket. A multipart ticket that expires aborts its upload
and discards its parts. Abandoned multipart uploads are removed by the bucket's lifecycle rule (**1 day** on
R2); a deployment on AWS or Garage needs the equivalent rule configured, or the unfinished parts stay in the
bucket. The `ETag` is exposed to the browser by the bucket's CORS policy (see "The bucket CORS policy").

**The plan quota is the real limit, and nothing is rounded up.** The quota is checked on the claimed size at
ticket time, net of the file being replaced, and re-checked at completion against the size storage actually
holds; an upload that overshot is refused and its object deleted, never silently recorded. `Create` and
`Complete`/`Abort` are server-side calls — the bucket's CORS policy allows only `GET`/`HEAD`/`PUT`, so the
client can never complete or abort an upload itself.

**The form fallback is still bounded at 100 MB.** `POST /api/v1/projects/{projectId}/files` (the
multipart/form-data action, used when a deployment cannot presign and by non-browser clients) still sends its
bytes through nginx and Cloudflare. **No nginx route raises its cap for that path**: the server-level
`client_max_body_size` (`CLIENT_MAX_BODY_SIZE`, default `100M`) applies, and Cloudflare Free rejects a body
over 100 MB. The direct routes exist to take the bytes out of that path; the fallback's own error says so
plainly.

## What finalisation verifies

The order is the contract. Every step fails closed, and every refusal that happens after the object
exists deletes it rather than leaving bytes no row points at.

1. **Access.** `_projectAccess.GetAsync(projectId, ProjectPermission.Edit)` — the same call,
   and the same 404 (another organization, or no such project) / 403 (role too low) bodies, as the
   multipart action. This is the check that is never relaxed: a presigned URL is a capability to write.
2. **The key.** It must be a normalized key under `projects/{projectId}/` **and** byte-for-byte
   the key that the ticket's own file id, file name and folder derive; otherwise
   `400 UPLOAD_KEY_MISMATCH`, and storage is not touched at all.
3. **The ticket.** The MAC is recomputed over the presented tuple: `400 UPLOAD_TICKET_INVALID`
   for a fabricated, edited or another caller's ticket.
4. **Expiry.** `410 UPLOAD_TICKET_EXPIRED`, and the object is discarded.
5. **Existence, and the move off the presigned key.** A HEAD. `409 UPLOAD_OBJECT_MISSING` when nothing
   was ever written — a completion claim with no bytes behind it must not produce a row pointing at nothing.
   A single PUT's key is a **staging** key: the HEAD pins its ETag, and the object is moved with a
   conditional server-side copy (`IFileStorageService.MoveIfMatchAsync`) to a fresh key that was never
   presigned. If the staged object changed between the HEAD and the copy (bytes re-PUT through the URL while
   the completion ran), or the store cannot condition the copy, the answer is `409 UPLOAD_CHANGED` and both
   objects are deleted. Every check below, and the row, describe the moved copy.
6. **Size.** The real size comes from the HEAD, never from the body. `413 FILE_TOO_LARGE` above
   `DirectUploadLimits.MaxObjectBytes` — 5 TiB, the same value a ticket returns as `maxBytes` — and the
   object is discarded. A size that disagrees with the client's claim is
   `409 UPLOAD_SIZE_MISMATCH`: the bytes that arrived are not the bytes the ticket was issued
   for, so the object is deleted and nothing is recorded.
7. **Quota.** `IPlanGateService.CheckUploadAsync` runs again, against the real size, with the
   same `413 STORAGE_QUOTA_EXCEEDED` body. Step 1 could only check a claim, and a presigned PUT
   cannot carry a maximum length — R2 and Garage do not sign one, which is exactly why
   `PresignedUrlService.PromoteAsync` enforces the runner's cap server-side too. An upload that
   overshot is deleted, never silently recorded.

Only then is the row written: the same fields, the `userFiles` category, the resolved MIME
type, R22 (a video/audio upload is never queued into the text summarizer or the vector indexer) and the
indexing/summarization notifications the multipart action produces. A repeated completion is idempotent —
the ticket's file id is the primary key, so a retry returns the first call's row (200) instead of failing
or creating a second one.

**A `.ts` is typed by its bytes.** `.ts`, `.mts` and `.cts` are TypeScript as often as MPEG transport
streams, and every Windows browser (and `FileExtensionContentTypeProvider`) declares them
`video/vnd.dlna.mpeg-tts`. When the declared type for such a name is a transport-stream type, generic or
absent, finalisation reads the first 8 KB of the stored object (`TransportStreamMimeSniffer`): the MPEG-TS
packet pattern (sync byte `0x47` every 188 bytes, or after a 4-byte prefix every 192 for M2TS) keeps a video
type, readable text is recorded as `text/plain` (and is summarized and indexed like any text upload), and
other bytes keep the declaration. A read failure keeps the declaration; it never fails the upload. The form
upload, the URL import, the GitHub pull, cloud-drive sync and the model write tools apply the same rule, and a
one-time startup pass (`TransportStreamMimeBackfillService`, `ProjectFiles:TransportStreamBackfill:*`) re-types
rows stored before it (issue 8, 2026-11-03).

**Why the move.** A presigned PUT cannot be revoked. Before the move, a URL replayed after completion
overwrote the recorded file in place, so moderated bytes could be swapped for unscanned ones without a new
completion (no CSAM scan, no moderation, no quota re-check). Now a replay only re-creates an unreferenced
object at the staging key (a retried completion deletes it, the retention sweep's orphan pass catches the
rest), and a recorded file's bytes change only through a new ticket and a new completion. A retried
completion of a relocated row keeps its verdicts, since its bytes cannot have changed. A **multipart**
upload is not moved (except an assistant ticket's, S-05): its part URLs are signed for the upload id, which
storage forgets once the upload is completed, so they cannot write after completion.

## Which route the browser takes

**Direct first; the form action only when no bytes could have landed.** `web/lib/api/files.ts` uses the
direct route for every upload, and for a multipart ticket it PUTs the parts itself — **3 at a time**, with
one aggregate progress bar, reading each part's `ETag`, attempting a failed part up to **3** times, and
aborting the upload on cancel or failure. It falls back to `POST …/files` in exactly two cases, and reports
every other failure to the user:

- the API answered **501 `DIRECT_UPLOAD_DISABLED`** — this deployment has no presigning wired,
  so there is nothing to fall back from; or
- the storage PUT failed at the **network level with zero bytes sent** (`xhr.status === 0`,
  `upload.onprogress` never fired) — a rejected CORS preflight, which is what a self-host
  install has until its owner configures CORS, or a dead network.

A 403 from storage, a 400/409/413 from the API, or a network failure *after* progress was observed is
reported rather than retried: bytes may already be in the bucket, and re-sending the file through the API
would hide the reason. The fallback is therefore the self-host path, and the path for a non-browser client,
while the direct routes (single PUT and multipart) are the default everywhere else — but the fallback is the
one bounded at 100 MB by nginx and Cloudflare, so a large file cannot travel it at all.

## The bucket CORS policy

A browser PUT to `<account>.r2.cloudflarestorage.com` is cross-origin, so the bucket needs a
CORS rule or the preflight fails before a single byte is sent. That is bucket configuration and cannot be
done from this repository at runtime; `cloud-storage.md` is the operator reference for the bucket
as a whole.

For the staging environment the rule is `browser-presigned-get-put`:

- **Allowed origins:** `https://reelbolt.example.com` — the value of `app_hostname` for
  that environment, which the module interpolates into `cors_allowed_origins`. It must be the
  exact scheme and host the dashboard is served from; a mismatch is a failed preflight, not a failed
  upload.
- **Allowed methods:** `GET`, `HEAD`, `PUT`.
- **Allowed request headers:** `content-type`, `content-length`, `range`,
  `x-amz-content-sha256`, `x-amz-date`, `authorization`. A browser sends
  only `content-type` here (the signature is in the query string, and `content-length`
  is a forbidden header a browser manages itself), but the rest are needed by the desktop and cloud
  runner clients that share the rule.
- **Exposed response headers:** `ETag`, `Content-Length`, `Content-Range`,
  `Accept-Ranges`.
- **Preflight cache:** `max_age_seconds = 3600`.
- **Jurisdiction:** the bucket's (`eu`).

An `OPTIONS` preflight is answered by R2 itself from this rule — not by the Inference API and
not by nginx. Verify it before trusting an upload:

```bash
curl -sS -i -X OPTIONS "https://<account>.r2.cloudflarestorage.com/<bucket>/<any-key>" \
  -H "Origin: https://reelbolt.example.com" \
  -H "Access-Control-Request-Method: PUT" \
  -H "Access-Control-Request-Headers: content-type"
```

A pass is `204` (or `200`) with
`Access-Control-Allow-Origin: https://reelbolt.example.com`,
`Access-Control-Allow-Methods` containing `PUT`, and
`Access-Control-Allow-Headers` containing `content-type`. A missing
`Access-Control-Allow-Origin` (or a 403) means the rule is absent, the origin does not match
exactly, or the bucket is not the one the `cors_allowed_origins` change was applied to — in
which case the browser falls back to the multipart route and the upload still works, just through the
100 MB path, which is where a 500 MB source video still fails.

## Expiry: one hour for the URL, an hour and a half for the ticket, a day for a multipart upload

`ProjectFilesController.UploadUrlTtl` is `DirectUploadService.SinglePutUrlTtl` = **1 hour** (it was 4 hours
until the upload-replay fix), and a single-PUT ticket lives for **1 hour 30 minutes**. The runner's
30-minute staging cap is deliberately not reused: that window covers a transfer between two machines on a
datacentre link, while this one has to cover a phone. A single PUT is at most 256 MiB, which an hour carries
at about 0.6 Mbps; at 2 Mbps it takes about 17 minutes — a presigned URL is a bearer capability, so it
should not outlive the task by much. SigV4's own maximum is 7 days; this is well inside it. The ticket
outlives the URL by 30 minutes so that a client which finishes its PUT in the last minute can still
record the file; the URL is dead by then, so the object cannot change during the grace period.

A **multipart** ticket instead lives **24 hours** (`DirectUploadService.MultipartTicketTtl`), because a
multi-gigabyte file can genuinely take a day on a slow uplink, and each batch of part URLs is valid for at
most 4 hours and never past the ticket. Its upload id and parts are removed by the bucket's lifecycle rule
after a day if it is never completed (see "Large files go up in parts").

## What this does not cover

- **An object with no row.** If a browser uploads and never confirms — a closed tab, a crash — the bytes
  are orphaned. The key is unique per upload, so an orphan can never be mistaken for a live file, is
  never listed (the file list comes from `project_files`) and is not counted against the plan
  quota (which sums that table). It does cost storage. A reconcile sweep comparing
  `projects/*/userFiles/*` against `storage_key` — the shape
  `StorageRetentionService` already uses for expiry — is the fix if it ever matters. Every
  refusal after the object exists deletes inline, so the orphans are the abandoned ones only.
- **Authoritative S3 user metadata.** The multipart route also stores `project-id`,
  `owner-id`, `uploaded-by` and friends as object metadata. The direct route cannot:
  the browser would have to author them, the presigned PUT would have to sign seven
  `x-amz-meta-*` headers, and every one of them would have to be allowed by the CORS rule —
  for a column no code reads. The same facts are recorded server-side in
  `project_files.storage_metadata_json`, which is better provenance than a client-authored
  header, but the object itself carries only its content type.
- **A file whose bytes are the wrong bytes.** Finalisation proves the key and the size, not the content.
  That is unchanged from the multipart route, which also trusts the client's bytes.

## Sample clip (no upload at all)

`POST /api/v1/projects/{projectId}/files/sample` with `{"sampleKey":"first-film"}` copies a bundled, generated
first-party clip (24 s, under 1 MB, see `inference/src/ReelBolt.Inference.Api/Assets/samples/README.md`) into the
project as `Sample clip.mp4`, so a new user can try a workflow without a file of their own. Edit access and a verified
email are required (like `import-url`); the plan's storage quota applies. `201` with the same file object an upload
returns, run through the normal post-add path (probe, derivatives, moderation). Idempotent: a repeat answers `200`
with the existing file and stores nothing. An unknown `sampleKey` is `400 SAMPLE_UNKNOWN`; a project you cannot see is `404`.
