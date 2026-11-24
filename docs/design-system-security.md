INTERNAL BACKGROUND: not for customers.

# Design-system security: safe serving, bounded reads, fenced prompts and a hardened zip reader

Phase 0 closes one class of gap in four places: untrusted project bytes reaching a browser, the
background indexer, a model prompt, and an archive parser. This page states what the code does
today. Every `##` section below stands on its own.

## Why this exists

Project files are user-supplied bytes, and four different code paths used to trust them: the API
streamed them with a content type a browser could sniff, the indexer read a whole file into memory,
the summarizer pasted file content into a model prompt with no fence, and there was no shared zip
reader with entry, size, ratio and path limits. Phase 0 adds `SafeContentPolicy`,
`BoundedTextReader`, `FileSummaryPromptBuilder` and `SafeZipReader` and wires the first three into
the existing routes, the file indexer and the summarizer. `SafeZipReader` is also used by the `import-worker` service (see **Residual risks**).

## Serving project files

`SafeContentPolicy` (namespace `ReelBolt.Inference.Api.Services.Storage`) decides how every
project-file stream is labelled. A file is served `inline` only when its MIME is one of
`image/png`, `image/jpeg`, `image/gif`, `image/webp`, `video/mp4`, `video/webm`, `video/quicktime`,
`audio/mpeg`, `audio/wav`, `audio/x-wav`, `audio/ogg`, `audio/aac`, `audio/mp4`, `audio/webm` **and**
its file extension is consistent with that MIME. Everything else is `application/octet-stream` with
a `Content-Disposition` of `attachment`.

Every project-file stream carries the same four headers: `X-Content-Type-Options: nosniff`,
`Content-Security-Policy: sandbox; default-src 'none'`, `Cross-Origin-Resource-Policy: same-origin`
and `Referrer-Policy: no-referrer`.

The routes covered are `GET /api/v1/projects/{p}/files/{f}/media` (streamed through
`RangedObjectStreamer`), `GET .../outputs/{id}/download`, media derivative content and sheet content,
editor layer objects, and `GET .../files/{f}/download` (always `attachment`). Step-result artifacts and
the timeline artifact, the design-system export (project and library) and the edit-timeline export carry
the same four headers with an `attachment` disposition: they stream bytes the server generated, so they
need no MIME decision. `ServingPolicyCoverageTests` reads the controller sources and fails when any
action that calls `File(` or `StreamAsync(` skips the policy, so a new streaming route cannot ship without it.

File names pass through `SanitizeFileName`, which strips `"`, `\`, `/`, CR, LF and control
characters and caps the result at 150 characters, and the response carries an ASCII-only
`Content-Disposition` with a `filename*` parameter.

## Bounded reads

`BoundedTextReader.ReadAsync(stream, maxBytes, ct)` lives in `ReelBolt.Shared.IO` and is the read
path for text the platform copies out of storage. It reads at most `maxBytes` bytes and reports
whether the input was truncated.

Two configuration keys set the caps. `FileIndexing:MaxTextBytes` defaults to `4194304` (4 MiB) and applies to text only: a file whose
stored MIME starts with `text/` and is over the cap becomes `FileIndexingStatus.Failed` with error
`too_large` without a download, and any other file is classified from its head first, so a large image,
video or PDF is no longer failed as `too_large`. If the stored size lied and the text read is truncated,
the file is also `Failed` with `too_large`. `FileSummarization:MaxTextBytes` defaults to `524288` (512 KiB): the text is cut and
`\n[truncated]` is appended.

## Summarizer prompt

`FileSummaryPromptBuilder` builds the summarizer turn from a fixed template that wraps the file body
in `[FILE CONTENT]` / `[END FILE CONTENT]`. The file name is sanitized and capped at 200 characters.
Any occurrence of the two fence markers inside the name or the content is neutralized to
`[FILE-CONTENT]` / `[END-FILE-CONTENT]`, so file-controlled text cannot close the fence early.

The `FileSummarizer` agent prompt pair is unchanged by this fix.

## Zip reader

`SafeZipReader.Read` (`ReelBolt.Shared.Archives`) is the only zip reader in the platform. Its limits
are `MaxEntries 2000`, `MaxTotalBytes 67108864`, `MaxEntryBytes 8388608`, `MaxRatio 100`,
`MaxDepth 12` and `MaxArchiveBytes 33554432`. Every byte actually inflated counts toward
`MaxTotalBytes`, including bytes of entries that end up rejected, and the reader inflates each entry
itself instead of trusting the declared size: an entry whose real size differs from its header is
rejected, so a lying size cannot slip through.

An archive can abort with `ARCHIVE_TOO_LARGE`, `ARCHIVE_INVALID`, `TOO_MANY_ENTRIES`,
`TOTAL_TOO_LARGE` or `BOMB_SUSPECTED`. A single entry can be rejected with `PATH_TRAVERSAL`,
`PATH_INVALID`, `PATH_TOO_DEEP`, `DUPLICATE_PATH`, `NORMALIZATION_UNAVAILABLE`, `SPECIAL_FILE`,
`ENTRY_TOO_LARGE`, `RATIO_EXCEEDED`, `ENTRY_UNREADABLE` or `NESTED_ARCHIVE`. On abort, `Entries` is empty.

The only caller is the `import-worker` service (`ExtractZipHandler`, `POST /v1/extract-zip`). Parsing of zip, PDF
and font files must never run in the API or the engine process.

## Tests that guard this

These tests run in `tests/ReelBolt.WorkflowEngine.Tests`:

- `SafeContentPolicyTests` pins the allowlist, the extension check, the headers and the sanitizer.
- `RangedObjectStreamerTests` pins the media route's header and disposition behaviour.
- `CrossTenantControllerMatrixTests` keeps the download and content routes owner-scoped.
- `BoundedTextReaderTests` and `BoundedReadsConsumersTests` pin the caps and the failure mapping.
- `FileSummaryPromptBuilderTests` pins the fence, the name cap and marker neutralization.
- `SafeZipReaderTests` (with `ZipFixtures`) pins every abort and per-entry rejection above.
- `ServingPolicyCoverageTests` pins that every byte-streaming action goes through the policy.
- `SecretRedactorTests` and `DesignTextTests` pin the redaction and text-sanitising behaviour described below.
- `SvgSanitizerTests` pins the allowlist rebuild against the hostile corpus (see **The hostile corpus**); `DesignSystemAssetRouteTests` and `LibraryAssetRouteTests` pin the serving CSP, the re-sanitize-on-read rule and the upload refusals.
- `MarkupTextTests`, `PreviewCardReaderTests`, `ClaudeDesignReferenceTests` and `ClaudeDesignFontTests` pin the inert-text reduction, the card channel and the font channel.
- `DesignImportCapabilitiesTests` fails the build when an importer gains or loses a matrix row; `DesignImportReportTests` pins the axis counts and the bounded reason list; `DesignImportersRouteTests` pins `GET /api/v1/design-system/importers`.

## Known limits

The **generic** project-file policy never serves SVG or HTML inline: `SafeContentPolicy` has no
`image/svg+xml` entry and no inline extension rule for it, so a project file of either type goes out
as `application/octet-stream` with `attachment` from the media and download routes. The one route
that serves an SVG inline is the design-system asset route, and only after `SvgSanitizer` has rebuilt
the stored bytes again on read (see **SVG allowlist**); the library content route does the same. The
inline allowlist of the generic policy is deliberately small, and adding a format means adding both a
MIME and its extension rule. Nothing renders or thumbnails an SVG: the sanitizer rebuilds it, it is
not rasterized.

## Secret redaction and design text

Imported text is data, and two shared helpers clean it before it is stored or shown to a model.

`SecretRedactor.Redact` replaces each match with `[redacted]`. Every pattern is a linear-time
`NonBacktracking` expression, so hostile input cannot make it slow. It covers:

- a private-key PEM block from its `BEGIN` line through its `END` line, and a bare `BEGIN` header from a truncated block;
- `Bearer` tokens, JWTs and `scheme://user:password@` URL credentials;
- the key shapes of OpenAI, Stripe, GitHub (classic and fine-grained), Google, Figma, SendGrid and Slack;
- AWS access key ids (`AKIA`/`ASIA`) and a 40-character AWS secret that follows a `secret` or `aws` word and a `:` or `=`.

`DesignImportRegistry` applies it to every string value **and every JSON key** of the design document
and the product map, to diagnostic messages and diagnostic paths, and to source paths. The product map is
also redacted inside `ProductMapExtractor.Extract`, so import, drift and the step executor all receive a
map that is already clean.

`DesignText.Sanitize` drops the markup characters that could close a prompt fence or inject structure
(square, angle and curly brackets, the double quote, the backtick, the backslash and `#`) and their fullwidth and CJK lookalikes (for example U+FF3B and U+3010). It also drops characters
invisible to a reader: Unicode format characters (zero-width, bidi), the tag block U+E0000..U+E007F,
variation selectors (U+FE00..U+FE0F and U+E0100..U+E01EF) and the replacement character U+FFFD.

## Import worker hardening

The `import-worker` is the only place zip and PDF bytes are parsed. It holds no database, storage or
provider credential. Its controls, as the code and `docker-compose.yml` have them:

- **Network.** It sits only on the compose network `import-net`, declared `internal: true` and shared with the `inference` service and nothing else. It has no path to postgres, rabbitmq, qdrant or the internet, and it publishes no port.
- **Container.** `read_only`, `cap_drop: ALL`, `no-new-privileges`, `mem_limit: 512m`, `pids_limit: 128`, and a 64 MiB `/tmp` tmpfs mounted `noexec,nosuid,nodev`.
- **Concurrency.** `IMPORT_WORKER_MAX_CONCURRENCY` (default `2`, range 1 to 16) bounds simultaneous extractions. The slot is taken before the body is read; a request that finds none is answered `503` with `Retry-After` and is never queued.
- **PDF.** `pdftotext` is called with `-l`, capping pages at `IMPORT_WORKER_PDF_MAX_PAGES` (default `50`, range 1 to 500), on top of the 20 MiB, 20 s and 200 KiB stdout caps. A body without the `%PDF-` header is `415`.
- **Zip.** A body without a zip header is `415` (the API answers `400 NOT_A_ZIP`). The reader counts every inflated byte, rejects entries whose real size differs from the declared one, and accepts full Unicode entry names (accents, CJK, Cyrillic, emoji): names are normalised to NFC and de-duplicated on NFC plus invariant upper-casing, so `é` precomposed and decomposed are one name (the second is `DUPLICATE_PATH`), while Turkish `i`, `ı` and `İ` stay three distinct names. Control and format characters (Cc, Cf: the bidi overrides U+202A-202E and U+2066-2069, zero-width and tag characters), line/paragraph separators, unpaired surrogates, the replacement character, invisible fillers (Hangul fillers U+115F/U+1160/U+3164/U+FFA0, Braille blank U+2800, combining grapheme joiner U+034F, variation selectors), a Unicode space at the start or end of a segment, more than 32 combining marks in one segment, a path over 1024 characters (the API's own cap) and more than 200,000 normalised name characters per archive are `PATH_INVALID`; `COM`/`LPT` with superscript digits are reserved device names. The classifier decides `.js`/`.html` on the name with trailing blanks and invisible characters removed. That needs ICU: the import-worker image ships `icu-libs` (about 4.7 MiB installed, the worker image grows from 261 MB to 272 MB) with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`, and the worker's reader fails closed: where the runtime cannot normalise, a non-ASCII name is refused as `NORMALIZATION_UNAVAILABLE` rather than risk two spellings of one file. A `document.json` over the 2 MiB inline cap is reported as `DOCUMENT_TOO_LARGE`.
- **Scripts never cross the boundary.** Script files, `tailwind.config.*` included, are dropped as `DROPPED_SCRIPT` and their text is never sent to the API. A Tailwind config is reported by name with empty text, and the importer raises `TAILWIND_CONFIG_NOT_READ_FROM_ZIP`.

The API side treats the worker as untrusted too. `ImportWorkerClient` validates every entry of an
`extract-zip` answer and fails the whole answer with `502 IMPORT_WORKER_BAD_RESPONSE` on any violation
(the log carries a fixed code and a count, never worker content). A blank `Import:Worker:Token` or a blank
`Import:Worker:BaseUrl` answers `503 import_worker_unavailable`. The
`import-zip` response carries a `skipped` list and `FILES_SKIPPED` Info diagnostics for files the worker left out.
`Import:Pdf:Enabled` (default `true`) is a kill switch for the PDF route: when `false` it answers
`404 feature_disabled` after the project-access check. The Figma route answers the same `404` unless
`Import:Figma:Enabled` is `true` (default `false`); it requires a `fileKey` matching
`^[A-Za-z0-9]{10,64}\z` and an access token of at most 512 characters, reads the Figma body under a
20 s deadline and a 2 MiB cap (not 8 MiB), and never follows a redirect.

### Runtime smoke result

The hardened image was built from the review-fixes checkout and run on a throwaway `--internal` Docker
network with the same flags compose sets (read-only root, `cap-drop ALL`, `no-new-privileges`, 512 MiB, 128 pids,
`/tmp` tmpfs `noexec,nosuid,nodev`, no published port), with a random token that was not printed. Observed:

- `GET /health` answered `200`; `POST /v1/extract-zip` with the token answered `200` and listed the zip's `tokens.json`; the same call without a token answered `401`; a non-zip body answered `415`.
- `POST /v1/pdf-text` answered `200` with the extracted text.
- The root filesystem was read-only, a script made executable in `/tmp` failed with `Permission denied`, there was no route to the internet, and the process ran as an unprivileged user with no effective capabilities and `NoNewPrivs` set.
- The token appeared zero times in the worker log.

Limits of this check: the image build needed `--network=host` on the first retry because of a transient NuGet restore failure, and the compose stack itself and its healthcheck were not run (the equivalent `/health` request was). Host-to-container traffic still reaches an internal network, so the isolation depends on nothing being published.

## Bounded asset and text channels (Claude Design)

A Claude Design archive carries raster logos, SVG marks, font files, HTML prose and card markup. None
of it is ever executed. The archive is classified by **magic bytes first**; the file extension can
only make a decision stricter, never talk a binary into looking textual.

- **The binary channels.** An entry becomes `image` when its magic bytes are PNG, JPEG or WebP, and
  `font` when they are WOFF, WOFF2, TTF or OTF. The file name never decides the type:
  `ZipFileClassifier.MediaType` sniffs the same head and returns null for anything else. Only an
  image under an `assets` segment (or the exact root `.thumbnail` basename) and only a font under a
  `fonts` or `assets` segment crosses the boundary. Caps are shared by the worker and the API:
  `ArchiveAssetLimits` (2 MiB per image, 16 images, 8 MiB total) and `ArchiveFontLimits` (4 MiB per
  font, 8 fonts, 16 MiB total). The worker copies the bytes as bounded Base64 and never decodes,
  re-encodes or thumbnails them. An entry a channel cannot carry is added to the skipped list with a
  named reason (`IMAGE_NOT_ASSET_PATH`, `IMAGE_TOO_LARGE`, `IMAGE_CAP`, `FONT_NOT_ASSET_PATH`,
  `FONT_TOO_LARGE`, `FONT_CAP`) and is never emitted as a byte-less image or font — which the API
  refuses as `asset_shape`/`font_shape` — so the rest of the import still succeeds.
- **The text channels.** An SVG (`svg`) or an HTML/XHTML entry (`markup`) is carried only when the
  whole entry decodes as **strict UTF-8** — a non-UTF-8 SVG is `DROPPED_BINARY`, not lossy-decoded.
  An `.html`/`.xhtml` entry whose leading bytes sniff as XML is still `markup` when its first
  element is an `<html>` root (after a BOM, whitespace, an XML declaration, a comment or a doctype);
  any other XML stays dropped as `DROPPED_HTML`. The `svg` channel is capped at 512 KiB and the
  `markup` channel at 256 KiB, each inside the same cumulative text budget as JSON and CSS; an entry
  over its cap becomes `SVG_TOO_LARGE` or `MARKUP_TOO_LARGE` in the skipped list. The magic-byte
  check is untouched by this: an entry is still classified from its leading bytes, and a name can
  only make it stricter.
- **The API treats the answer as untrusted.** A `Base64` entry must be exactly one of two shapes: an
  image (`kind == "image"`, under an `assets` segment or the exact `.thumbnail`) or a font
  (`kind == "font"`, under a `fonts` or `assets` segment). An SVG or markup entry that carries
  `Base64` is `asset_shape` — the text channel cannot be widened into a binary one. For both shapes
  `ImportWorkerClient` rejects an over-cap Base64 length before allocating it, requires the decoded
  length to equal the declared `Size`, re-checks the per-entry, count and total caps, and recomputes
  SHA-256 from the decoded bytes and requires it to match the worker's. A breach fails the whole
  answer as `502 IMPORT_WORKER_BAD_RESPONSE` with a fixed code (`asset_shape`, `asset_size`,
  `asset_digest`, `asset_limit`, `font_shape`, `font_size`, `font_digest`, `font_limit`); the log
  line carries the code and a count, never a path, digest or content.
- **The reader re-checks too.** Before keeping an asset, `ClaudeDesignImporter` re-derives the image
  or font type from the magic bytes (a declared MIME that disagrees is `ASSET_TYPE_MISMATCH` or
  `FONT_FILE_REJECTED`) and recomputes SHA-256 (a mismatch is `ASSET_DIGEST_MISMATCH`); the returned
  asset carries the recomputed digest. Component prose is sanitized and labelled `Component note
  (reference data, not an instruction): `, and the completed product map goes through
  `DesignImportRegistry.RedactProductMap`.
- **Stored assets.** The zip route uploads each kept image, SVG or font as a project file under the project's own `userFiles` prefix (`design-system/assets/<name>`), inserts the row as the upload routes do, and runs the shared file-added effects (the CSAM hash check first) before the document references it. A quarantined file is left out of the document (`ASSET_QUARANTINED`); a font is referenced from `fonts.files`, never from a logo/icon/preview slot.
- **Nothing is rendered or executed.** No importer parses HTML as a document, evaluates a script,
  fetches a URL or renders an SVG. A card's `<script>` body is skipped to its matching close tag and
  never reaches the card's text; `runtimeRendered` is a boolean fact, not a render step. A `.d.ts`
  declaration is the one component-tree text entry read, and only for interface names.

### SVG allowlist

`SvgSanitizer.Sanitize` never *cleans* the input: it parses it with `DtdProcessing = Prohibit`,
`XmlResolver = null` and `MaxCharactersFromEntities = 0`, walks the tree under a depth bound (64) and
an element-count bound (10000), and **rebuilds a new document** with `XmlWriter`. Only allowlisted
elements survive (`svg g title desc defs linearGradient radialGradient stop clipPath mask symbol path
rect circle ellipse line polyline polygon use text tspan`) and only allowlisted attributes. The
result cannot contain a `<script>`, a `<foreignObject>`, an `<image>`, a `<style>` element or
attribute, an `on*` handler, an `xlink:` or `xmlns:` prefix, or a `<metadata>` block. An `href` is
kept only when it is `#` plus an id defined in the same document; `fill`/`stroke`/`stop-color` are
limited to `#rgb`, `#rrggbb`, `#rrggbbaa`, `none`, `currentColor` and `url(#local-id)`; `transform` is
limited to `matrix|translate|scale|rotate|skewX|skewY` over digits, letters and `. , -`; every
attribute value is capped at 4096 characters and stripped of control, bidi and zero-width characters.
Input and output are each capped at 512 KiB. A malformed, oversized, too-deep or drawable-less
document is refused (`SVG_INVALID`, `SVG_TOO_LARGE`, `SVG_EMPTY`) and nothing is stored. The C2PA
`<metadata>` block in a benign logo is simply not copied, while the visual paths survive.

**Serving re-sanitizes.** The design-system asset route
(`GET /api/v1/projects/{projectId}/design-system/assets/{fileId}`) and the library content route
(`GET /api/v1/org/design-libraries/{libraryId}/assets/{assetId}/content`) both run the sanitizer over
the **stored** bytes on every read and answer 404 when it cannot produce a document. A served SVG
carries `Content-Security-Policy: default-src 'none'; sandbox` (no script, no subresource, no
navigation) in addition to the shared `nosniff`, `same-origin` and `no-referrer` headers. The route
answers only for a non-quarantined file of the caller's project whose stored path starts with
`design-system/assets/`, buffers at most 4 MiB, serves `image/svg+xml` only for a stored
`image/svg+xml`, and serves a font as an `application/octet-stream` attachment. The generic media and
download routes never serve either inline.

### HTML carried as inert text

`MarkupText.ToText` is a bounded state machine, not a regex. Outside a tag it copies characters; it
skips a comment whole; it skips the body of `script`, `style` and `head` to the matching close tag (or
EOF); any other tag is skipped to `>`; entities are decoded, control and bidi characters removed and
whitespace collapsed. Input is capped at 256 KiB and output at 32 KiB. An unclosed container is
skipped to EOF, so a hostile document cannot leak the body it failed to close. The result is stored as
a `.txt` reference document; the markup itself is never stored or parsed.

### Font files

A font is magic-checked (WOFF, WOFF2, TTF, OTF), digest-checked by the API and the reader, and capped
at 4 MiB per entry, 8 entries and 16 MiB per channel. A file the worker channel cannot carry —
outside `fonts`/`assets`, over 4 MiB, beyond the entry count or the total — is dropped by name
(`FONT_NOT_ASSET_PATH`, `FONT_TOO_LARGE`, `FONT_CAP`) and the rest of the import still succeeds; a
font that crosses the channel but whose magic or declared digest disagrees is `FONT_FILE_REJECTED` at
the reader. The file stem names the family (a trailing weight/style run is removed); a file whose
stem is not a usable family is rejected. Only the family name and the presence of a `src` declaration
leave an `@font-face`/`@import` scan — the declaration's text is data and a URL in it is never
requested (`FONT_SOURCE_NOT_FETCHED`).

### Reference documents

Prose is uploaded as an ordinary project file under `design-system/references/<name>` with
`SummaryStatus`/`IndexingStatus` pending, so it enters the normal text-indexing path. Each document
is bounded at 256 KiB, at most 24 are kept and 2 MiB in total; the total passes the organization's
upload quota through `IPlanGateService.CheckUploadAsync`. Text is redacted by `SecretRedactor` before
it is stored, and a quarantined reference is omitted (`ASSET_QUARANTINED`). Every uploaded file runs
the shared file-added effects (the CSAM hash check first) before it is returned.

### Library-owned assets

`design_system_library_assets` holds inert bytes for an organization's brand library: a sanitized SVG
or one of the four font containers, at most 24 per library. A raster is refused
(`415 LIBRARY_ASSET_TYPE`) because the library route runs no image scan, so unscanned imagery never
enters a shared library; rasters remain project files, where the CSAM path already runs. The table
carries **no `Tenant` query filter** — it has no organization column — and is parent-guarded: every
route resolves the library against the caller's active organization first (`l.Id == libraryId &&
l.OrganizationId == orgId`), and the row cascades when the library is deleted. An SVG is sanitized at
upload and stored as the rebuilt document (so a successfully sanitized logo that carried C2PA
metadata is stored with the metadata dropped); an SVG the sanitizer cannot rebuild is
`422 LIBRARY_ASSET_UNSAFE`; the digest is computed server-side over the stored bytes. The uploaded
name is reduced to one path segment and sanitized (an empty name is `400 LIBRARY_ASSET_NAME`), the
upload passes the organization's upload quota and the asset's bytes count in its storage usage, and
the content route re-checks that the stored key starts with
`design-libraries/{orgId}/{libraryId}/assets/` before opening it. The content route re-sanitizes on
read and sets the same `default-src 'none'; sandbox` CSP; a font leaves as an attachment. A document
may name a library asset only through `library-asset:<guid>` and only when the validator's context
(built from the linked library) contains it.

### The hostile corpus

`inference/tests/ReelBolt.WorkflowEngine.Tests/TestData/design-import/svg-hostile/` is synthetic and
contains no client artwork. `SvgSanitizerTests.CorpusFiles` pins each file and its expected outcome;
the theory reads the directory, so a fixture added without a case fails, and a case naming a missing
file fails.

| Fixture | What it proves |
|---|---|
| `script.svg` | an inline `<script>` is dropped and the drawable path survives |
| `foreign-object.svg` | `<foreignObject>` and XHTML (with a nested script) cannot enter the rebuild |
| `event-attribute.svg` | every `on*` handler is dropped from the root and from an element |
| `external-href.svg` | a remote `xlink:href`/`href` on `<use>` is refused, not resolved |
| `use-external.svg` | a remote `<use>` is dropped while a local `#id` reference survives |
| `style-element.svg` | a `<style>` block cannot carry `url(javascript:)` into the output |
| `css-url.svg` | a `style` attribute and a paint's `url(javascript:)` are dropped; a local gradient survives |
| `entity-expansion.svg` | a DOCTYPE internal subset with recursive entities is refused (`SVG_INVALID`) |
| `doctype.svg` | an external DTD reference is refused, never fetched |
| `malformed.svg` | truncated XML is refused rather than repaired |
| `deep-nesting.svg` | 200 nested groups exceed the depth bound and are refused |
| `data-uri-image.svg` | an `<image>` with a `data:` URI cannot enter the rebuild |
| `image-element.svg` | an `<image>` with a remote URL cannot enter the rebuild |
| `smil-set.svg` | SMIL `<set>`/`<animate>` elements are dropped; the static shape survives |
| `processing-instruction.svg` | an `xml-stylesheet` processing instruction is never copied |
| `namespace-confusion.svg` | XHTML-namespaced `script` elements are refused by the namespace check |
| `c2pa-metadata.svg` | a benign logo's `<metadata>` is dropped and its visual paths survive |
| `plain-logo.svg` | a legitimate gradient, `<title>` and `<text>` survive the rebuild |
| `attributes.svg` | a hostile attribute beside each drawable path is dropped and the paths survive |

The `SvgSanitizerTests` also assert that no output contains a script, `foreignObject`, `javascript:`,
`onload=`, `data:`, an entity declaration, a processing instruction, an `xlink:` prefix or an
`<image>` element.

## Residual risks

These are the risks that remain after the controls above. Each is stated as the code has it.

- **`pdftotext` on untrusted PDFs.** The `import-worker` container runs poppler's `pdftotext` (`PopplerPdfTextRunner`) over uploaded PDFs. The runner clears the process environment, enforces `PdfMaxBytes` (20 MiB), a `PdfTimeoutSeconds` timeout (20 s), a page cap and a `PdfTextMaxBytes` stdout cap (200 KiB), and kills the process on the way out. The container limits are listed under **Import worker hardening**. The kill switch `Import:Pdf:Enabled` turns the route off without a deploy, and the poppler package is installed unpinned from the Alpine repository (`apk add --no-cache poppler-utils`). A parser bug in poppler is therefore contained by the container, not prevented.
- **Assistant and MCP cannot approve a design change.** Approving a proposal makes a version the one agents read, so a person must do it. `ApplyDecideDesignChange` refuses `approve` when the call arrives through the MCP bridge, when the user's `AssistantAutoApprove` is true, and when the account row cannot be read; `reject` stays allowed. In the in-app chat the approval card (built from the database and the exact arguments) still needs the user's click.
- **Library and tenant model.** Only `design_system_libraries` carries the named `Tenant` query filter. `design_systems`, the three child tables and `design_system_library_assets` are unfiltered: project routes guard the document tables at the project, and a library's container row or asset is reached through the organization-scoped library lookup (`OrgDesignLibrariesController`, `l.OrganizationId == orgId`). `DesignSystemService` also checks, for a library-scoped call, that `design_system_libraries` names the container for the scope's organization, so a hand-built scope with a foreign container id finds nothing; a document may name a `library-asset:` only when the validator's library-derived context contains it. A project transfer clears `design_systems.parent_library_id`.

## Release condition: IMPORT_WORKER_TOKEN

`IMPORT_WORKER_TOKEN` is mandatory. The `import-worker` service in `docker-compose.yml` declares `IMPORT_WORKER_TOKEN: ${IMPORT_WORKER_TOKEN:?...}`, so **every** `docker compose` invocation that reads the file fails until the variable is set, on every box, including the media box. The worker also exits at startup when the token is unset. Before the next production deploy, add `IMPORT_WORKER_TOKEN` to the production `CONTROL_ENV_FILE` and `ENGINE_ENV_FILE` deploy secrets. The same condition is listed in [`secrets.md`](secrets.md).
