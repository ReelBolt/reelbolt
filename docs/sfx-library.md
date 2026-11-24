# Built-in sound-effects library

A licence-clean library of short sound effects that every project can use at no per-use cost. A `VideoAnalyze`
step with `OfferSfxClips` and `IncludeSfxLibrary` offers the library next to the project's own audio files, a
`SoundDesigner` picks by event type, and `VideoCompile` mixes the chosen sound
(see [video-editing.md](video-editing.md#the-built-in-sound-library)).

## What is in it

The library has two origins.

**Recorded pack sounds (CC0).** A curated subset of five packs by Kenney, each released under CC0 1.0 (see
[sfx-library-credits.md](sfx-library-credits.md)), is committed as small, normalised Ogg Vorbis files under
`inference/src/ReelBolt.Inference.Api/Services/SfxLibrary/pack/` (under 1 MB in total). CC0 allows redistribution.
The manifest pins each file by SHA-256 and records its proof; the seeder refuses a file whose hash differs.

**First-party synthesized effects** fill the categories the packs lack (the camera shutter, typewriter, a sparkle or
a stamp in the right style). Each is rendered at
boot by ffmpeg from a recipe table in code (`SfxSynthRecipes`): `aevalsrc` tones and seeded `anoisesrc` noise,
fixed filters and fades, loudness-normalised to one level, mono 48 kHz 16-bit WAV, 0.15 to 2.5 s. The same recipe
version produces the same bytes. Categories: pop, click, whoosh (short and long), swoosh in and out, ding and
chime, stamp, thud, impact (soft and hard), shutter, riser, glitch, zap, sparkle, text tick and typewriter, and
scene-swap sweeps.

Tags carry the event types a sound suits: `appear`, `disappear`, `text_reveal`, `transition`, `hit`, `flash`,
`scene_swap`, `stamp`, `pop`.

## Provenance and licence

| Field | Synthesized items | Pack items |
|---|---|---|
| Provenance | `first-party synthesized, recipe v1` (`recipe_version` = `v1`) | `recipe_version` = `pack:<hash prefix>`, original file name in the manifest |
| Licence | `ReelBolt-original` | `CC0 1.0`, <https://creativecommons.org/publicdomain/zero/1.0/> |
| Author | `ReelBolt` | Kenney (kenney.nl) |

Pack licence proof, retrieved 2026-11-10. The `License.txt` shipped in each pack zip was read and says:

| Pack | `License.txt` wording |
|---|---|
| Interface Sounds (1.0) | "License: (Creative Commons Zero, CC0) http://creativecommons.org/publicdomain/zero/1.0/" |
| Impact Sounds (1.0) | "License: (Creative Commons Zero, CC0) http://creativecommons.org/publicdomain/zero/1.0/" |
| Sci-Fi Sounds (1.0) | "License: (Creative Commons Zero, CC0) http://creativecommons.org/publicdomain/zero/1.0/" |
| UI SFX Set | "License (Creative Commons Zero, CC0) http://creativecommons.org/publicdomain/zero/1.0/" |
| Digital Audio | "License (Creative Commons Zero, CC0) http://creativecommons.org/publicdomain/zero/1.0/" |

The pack pages are `https://kenney.nl/assets/<pack-name>` (`interface-sounds`, `impact-sounds`, `sci-fi-sounds`,
`ui-audio`, `digital-audio`). The pack files were read from the downloaded archives; the web pages themselves could not
be fetched by script, so re-check the page wording before relying on it for a legal question.

Changing any number or filter in a recipe changes the sound: bump `SfxSynthRecipes.Version`; the seeder then
re-renders every synthesized item and replaces its object (same id, new hash).

## Storage and data

* Table `sfx_library_items` (API-owned, migration `AddSfxLibrary`; the engine maps it read-only). Platform
  content: no organization column, no tenant filter. Columns: id, slug (unique), title, category, tags (jsonb
  array), duration_ms, size_bytes, sha256, storage_key, mime_type, licence, licence_url, author, source_url,
  recipe_version, is_enabled, sort_order, added_at.
* Bytes live under `platform/sfx-library/` in the object store: `{slug}.wav` for a synthesized sound and for a pack
  sound downloaded and normalised at boot, `{slug}.ogg` (`audio/ogg`) for a committed pack sound, which is stored as
  it is. All the pack sounds shipped today are committed Ogg files. That prefix is the only place the library may
  read or write; no API response, view or plan carries a key.
* The Inference API seeds at boot (`SfxLibrarySeeder`, idempotent): a row is rendered only when it is new, its
  recipe or source revision changed, or its object is missing. Each row is saved right after its object is written,
  so a replaced object and its row hash disagree for one item at most (the engine refuses bytes whose hash differs
  from the row), and a failure part-way keeps what was already written. The API image has ffmpeg and migrates before
  the engine starts, so the API owns rendering. A missing ffmpeg or unreachable store logs and leaves the library as
  it was; it never fails the boot. After a failed save the seeder lets go of its own rows, so the seeders that run
  after it on the same database context are not affected. `SfxLibrary:Enabled` (default `true`) switches seeding
  off.
* Configuration keys (Inference API): `SfxLibrary:Enabled`, `SfxLibrary:ManifestPath` (default: the manifest next to
  the binaries), `SfxLibrary:PackDirectory` (default: the `pack/` directory next to it) and `SfxLibrary:PackBaseUrl`
  (only for pack items that are downloaded, not committed).

## API

* `GET /api/v1/sfx-library` (signed in, any member): `query`, `category`, `tag`, `page`, `pageSize` (at most 100);
  returns items, total and a category facet. Only enabled items; no storage key, no hash.
* `GET /api/v1/sfx-library/{id}/preview`: the sound's bytes inline (WAV or Ogg, by the row's MIME type),
  Range-aware, under the shared content policy. An unknown or disabled item, and one whose object is gone, is the
  same empty 404. A storage error while reading the object is a 500.

* `GET /api/v1/sfx-library` also takes `maxDurationMs` (only sounds up to that length).
* `POST /api/v1/projects/{projectId}/sfx-library/{id}/add` (Edit permission on the project, one access consult):
  copies the sound into the project and answers `{ mediaRef: "file:<guid>", fileId, name }`.
* `POST /api/v1/projects/{projectId}/sfx-library/starter-set` (same permission): copies the starter set, about 24
  sounds picked round-robin over the categories in library order, and answers the added files and how many were new.
* Both add routes are rate-limited per user (`429 SFX_LIBRARY_RATE_LIMIT`) to `SfxLibrary:MaxAddsPerMinute` adds a
  minute (default 60). A project holds at most `SfxLibrary:MaxPerProject` library sounds (default 200); past that the
  answer is `409 SFX_LIBRARY_PROJECT_CAP`. The limit and the cap both live in the adder shared by REST, assistant and
  MCP; the limit is counted per API process, and a starter-set call costs one hit.
  An unknown or disabled item is the same empty 404 as the preview.

There is no admin route.

## Adding a sound to a project

All three ways in (the editor panel, the two routes above, and the assistant or MCP tool) go through one class,
`SfxLibraryProjectAdder`, so the rules are in one place.

* **A copy, made on the server.** The source key comes only from the library row; the destination is
  `projects/{projectId}/userFiles/sound-effects/{slug}.{ext}`, built from the row's validated slug and extension, never
  from a request. The bytes are copied inside the object store; nothing is downloaded or re-uploaded.
* **An ordinary audio project file** with `SourceKind = "sfx_library"`. It is the only place that registers a
  project file as already cleared (moderation Clean, CSAM Clean), because the content is first-party and curated.
  A request for the waveform peaks is published after the commit, like for any upload, so the timeline can draw it.
* **Idempotent.** The key is deterministic: adding the same sound twice finds the first file and returns it.
* **Not counted against storage.** `StorageUsageService` leaves `sfx_library` files out of the project and
  organization totals, so adding sounds never blocks an upload or moves a plan limit.
* **Every workflow can use it.** The file is a normal audio file, so `OfferSfxClips` offers it next to the user's own
  audio, with no `IncludeSfxLibrary` needed for sounds already in the project.

## The editor panel

The media pool has a **Sound effects** tab beside the media list (phone: **Sound effects** in the add-media
sheet). It searches by name, tag and category, filters by length, plays a sound on tap or click (never on hover),
and has **Add** per sound plus **Add starter set**. Adding puts the file in the pool; the usual placement puts it
on an audio track at the playhead. Strings are in `editor-media.json` (`sfx.*`), English and Italian.

## Assistant and MCP

`SearchSoundEffects` (free read) and `ProposeAddSoundEffect` / `ApplyAddSoundEffect` (MCP: `AddSoundEffect`,
approval-gated, `starterSet: true` for the starter set) let the assistant find a sound by name and copy it into a
project. They call `SfxLibraryProjectAdder`, re-read Edit permission on every call and use the propose-then-apply
token. See [platform-assistant-mcp.md](platform-assistant-mcp.md#sound-effects-search-the-library-add-a-sound).

## Takedown

`is_enabled` is the takedown switch. A sound that is off stays off: the seeder never switches a sound back on.

* To take a sound down at once, without a deploy, set `is_enabled = false` on its row. That survives every boot.
* To take it down for good in the release as well, remove a pack sound's entry from the manifest, or add
  `{ "kind": "synth", "recipe": "<slug>", "enabled": false }` for a synthesized one. A boot whose manifest was read
  switches off every sound that manifest does not list (or lists as not enabled).
* To put a sound back, set `is_enabled = true` on its row by hand, after the manifest lists it again.

A boot whose manifest is missing or invalid (one bad entry makes the whole manifest invalid) changes no switch at
all: the synthesized sounds still seed, the sounds that were on stay on and the ones taken down stay down. It logs
an error; fix the manifest and restart.

Known limit: an older release that boots against a newer table (a rollback, or the old slot restarting during a
deploy) reads its own, older manifest and switches off the sounds it does not list. They stay off until someone
sets `is_enabled = true` on those rows; no boot does it. Check the list after a rollback.

A disabled item disappears from the list, the preview and every new offer, and a compile drops a cue that names it
(`sfx_library_item_unavailable`).

## Cost

Free: no usage event, no credit, no quota. The step cost estimate does not change.

## Adding a CC0 pack

The manifest `Services/SfxLibrary/sfx-library.manifest.json` accepts two item kinds, so a recorded pack drops in
with no code change.

```json
{ "version": 1, "items": [
  { "kind": "synth", "recipe": "pop-soft", "enabled": true },
  { "kind": "pack", "slug": "pack-click-001", "title": "Click 1", "category": "click", "tags": ["click", "appear"],
    "file": "interface/click_001.ogg", "source_url": "https://...", "author": "...",
    "licence": "CC0 1.0", "licence_url": "https://creativecommons.org/publicdomain/zero/1.0/",
    "licence_proof": "quoted text from the official page", "retrieved_at": "2026-11-10",
    "source_sha256": "<sha-256 of the file as downloaded>" } ] }
```

A committed file carries `sha256` (of the stored bytes), `duration_ms`, `mime_type` and `original_file` instead of `source_sha256`, and its normalised bytes sit in the `pack/` directory. Rules the reader enforces: a pack item must be CC0 (anything else makes the whole manifest invalid, so nothing
unverified is published), `source_url` and `licence_url` are https, the proof text and retrieval date are
present, and the SHA-256 is of the downloaded file. Before adding an item, open the OFFICIAL licence page of the
pack, quote its licence name and version and URL, and record the date. Attribution-required licences are not
accepted.

Set `SfxLibrary:PackBaseUrl` to the place that serves the pack files; the seeder downloads
`{PackBaseUrl}/{file}`, refuses a file whose SHA-256 differs from the manifest, trims silence, normalises it to
the library format and stores it with `recipe_version = pack:<hash prefix>`. With no `PackBaseUrl`, pack items are
skipped and the synthesized items still seed.
