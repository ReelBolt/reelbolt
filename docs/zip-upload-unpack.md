# Zip upload unpacking

A `.zip` uploaded to a project is unpacked into real project files, so the code analyzers and the assistant read source instead of one opaque binary. The customer page is [`user-guide/projects/give-your-code.md`](user-guide/projects/give-your-code.md).

## Decisions

- The original zip row and object are **deleted** after a fully successful unpack. They are **kept**, with the reason shown, when unpacking fails. No unscanned media container stays downloadable, quota is not counted twice and analyzers see one copy.
- GitHub, cloud drive and computer-folder sync **never unpack**. A synced zip stays a file; readiness flags it (archives are not code).
- Unpacked files are not summarised or indexed (same as GitHub sync). The CSAM and moderation sweepers scan them like any upload.

## Pipeline

1. An upload completes (browser direct upload, form upload, URL import, assistant/MCP upload tools). A zip row is queued for unpacking.
2. A background worker in the API streams the object to the import worker (`POST /v1/unpack-zip`). The import worker reads it with `SafeZipReader` under the unpack limits and answers with a framed manifest (`application/vnd.reelbolt.unpack.v1`, magic `RBU1`, at most 1 MiB) plus the kept files.
3. The API client validates the framing and re-checks every number against `UnpackLimits`; anything off is a failure, never a partial result.
4. The API stores each kept file as a project file with its entry path in `directory_path`, then removes the zip row and object.
5. `POST .../files/{id}/unpack` runs the same pipeline again. It is the retry after a failure and the way to unpack zips uploaded before this feature (no backfill).
   The web app offers Retry only on a Failed note, so a zip uploaded before this feature has no button: it stays a plain file until someone calls that route directly (API or assistant), or deletes and re-uploads it.
6. The extracted files are scanned for CSAM before any derivative (thumbnail, probe) is made from them; the sweepers then moderate them like any upload.

## Caps (`UnpackLimits`)

| Cap | Value |
|---|---|
| Archive size | 64 MiB |
| Central directory entries listed | 20,000 |
| Files kept | 2,000 |
| Total unpacked | 128 MiB |
| One file | 32 MiB |
| Compression ratio | 100:1 |
| Path depth | 16 |
| Skipped-path samples in a manifest | 50 |

The plan storage quota applies to net growth (new files minus the removed zip).

## Exclusions (`ArchiveUnpackExclusions`)

Always skipped: `node_modules`, `bower_components`, `.git`, `.svn`, `.hg`, `__MACOSX`, `__pycache__`, `.venv`, `venv`, `.gradle`, `.idea`, `.DS_Store`, `Thumbs.db`, AppleDouble `._*`, `*.pyc`.

Build output (`dist`, `build`, `out`, `.next`, `.nuxt`, `.output`, `.svelte-kit`, `coverage`, `target`, `bin`, `obj`, `.turbo`, `.cache`, `.parcel-cache`) is skipped only when the zip also holds files outside those folders; a built-bundle-only zip keeps them. Skipped paths are counted by reason, with up to 50 samples.

## Folder naming

Files go to `<upload dir>/<zip base name>/`. One shared top-level folder inside the zip is stripped. A taken name gets ` (2)`, ` (3)`.

## Safety

Zip-slip safe: `SafeZipReader` rejects absolute, `..` and over-deep paths. Failure reasons are short upper-case codes (the client accepts `^[A-Z][A-Z0-9_]{0,39}$`), for example `ARCHIVE_TOO_LARGE`, `ARCHIVE_PASSWORD_PROTECTED` for an encrypted zip (the web shows its own message for each code), `MANIFEST_TOO_LARGE` when the manifest itself would pass its cap, and `UNPACK_BLOCKED` when the zip is held and cannot be unpacked (the web shows "This zip can't be unpacked", with no Retry; the reason is not shown).

## Readiness and analyzers

- Archives no longer count as code in readiness facts, lint rule WL010 or `GetProjectReadiness`. A project whose only code is an archive gets a "can't read this zip" message.
- The five code-reading analyzer agents fail fast in `CodeSourceGuard` before any model call or hold use, so nothing is metered or charged. Failure class `UserInput`, codes `NO_READABLE_SOURCE` and `CODE_ARCHIVE_UNREADABLE`.
