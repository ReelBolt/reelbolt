# Content moderation

ReelBolt classifies user text and images against a harmful-content policy through a
first-class inference capability, `InferenceProviderCapability.ContentModeration`. This page is
the developer and operator reference: where the feature is called, what it blocks, how it fails,
what leaves the host, and how to configure it. It is internal background, not buyer copy.

The feature is **opt-in**. `ContentModeration:Enabled` defaults to `false`; an install that has
not configured it keeps its previous behaviour.

> **This model is not a CSAM detector.** Never send known or suspected CSAM to the moderation
> client or to the vendor API. Child-safety handling is a separate, recognised hash-matching and
> reporting track. Read [CSAM exclusion and runbook](#csam-exclusion-and-runbook) before enabling
> the feature.

## Capability and resolution

`InferenceProviderCapability.ContentModeration` is an append-only enum member, persisted by name.
It is served by **`OpenAICompatible` providers only**; the admin API refuses to add the capability
to any other provider kind. An unresolved capability means "moderation is off" — it never falls
back to a chat deployment.

The first model is OpenAI `omni-moderation-latest`:

- Catalog model code: `omni-moderation-latest`.
- Request snapshot: `ContentModeration:Model`, default `omni-moderation-2024-09-26`. The alias
  moves under us; the pinned snapshot is what the client sends, and it is recorded on the audit
  row as `ModelSnapshot`.
- Endpoint: `POST {endpoint}/moderations`, with a trailing slash trimmed from the configured
  endpoint and `Authorization: Bearer <key>` sent only when a key is present.

`IInferenceProviderResolver.ResolveContentModerationAsync(Guid? explicitProviderId)` resolves
**platform-managed rows only**. A moderation answer is stored for good and decides what a person
may publish, so an organization's own provider row never answers it, whether it is the
organization's default or named explicitly, and whatever the organization's plan allows. The order
is:

1. The explicit provider id, but only when it is a platform row that carries `ContentModeration`.
   A Chat or Vision row id, or an organization's row id, written into a config cannot be used.
2. The platform's default row for the capability.
3. `null` when the platform has none.

The transcription done for moderation follows the same rule through
`ResolvePlatformTranscriptionAsync`: the platform's default transcription row, never the
organization's. Run-step transcription (a video-analyze step, voiceover alignment) keeps resolving
as before. This holds on every moderation surface: the API (ingest, publish, hand-off, uploads) and
the engine's gate.

`null` is not an error. The gate records the call as unavailable and applies the surface's
failure stance; publish/egress then fails closed when it is enabled.

A platform deployment seeds the default row from the `PlatformProviders` configuration array at
startup, the same way hosted ASR and embeddings are seeded. The seeder creates the platform
provider, model, capability and the `$0` price, and promotes the entry to the capability default
when `IsDefault` is true. It never deletes or replaces a stored key.

Moderation is metered but **free**. Every call writes one `usage_events` row with
`UsageKind.ModerationCalls` and `UsageSource.Moderation`, priced by a real
`model_capability_prices` row at `CostUsdPerUnit = 0`. That is a genuine $0 price, not an
unpriced capability: an unpriced platform capability would fail closed when billing is enabled
and would hide the integration from the ledger. The price book pre-fills the row, and the seed
catalog carries the same entry.

`ContentModeration:Enabled` is the only switch that turns moderation on. When it is `false`, every
gate call allows the action and publish proceeds. When it is `true` **without a resolvable
provider**, generation prompts, ingest and generated text fail open (the action proceeds and the
gap is recorded), while publish/egress fails closed. Enabling the feature is therefore a real
deployment decision: configure a provider in the same change.

## CSAM exclusion and runbook

**This model is a general harmful-content classifier, not a CSAM detector.** It must never be
used as a child-safety control, and known or suspected CSAM must never be sent to it.

- **Never send known or suspected CSAM to the moderation client or the vendor API.** OpenAI's own
  moderation guidance says the API is not a substitute for dedicated child-safety safeguards.
  Sending it would put the material in a third-party classifier and in the request path this
  client logs around — the opposite of a controlled child-safety process.
- **`sexual/minors` is a policy signal only.** The category is in the effective hard-block list,
  so a response that flags it blocks the action. That makes the surface fail closed on one
  category; it does not make this client, its audit rows or its review queue a CSAM detection or
  reporting system.
- **Child-safety handling is a separate track.** Suspected CSAM is handled by the recognised
  hash-matching and reporting procedure, outside this capability. This initiative adds no
  hash-matching, evidence-preservation or reporting code, and no audit row for CSAM.
- **Runbook — if you encounter suspected CSAM:** stop the normal moderation flow; do not submit
  the material to `IContentModerationClient`, the OpenAI-compatible endpoint, or any generation
  vendor; do not route it through a preview, retry or export. Preserve and escalate it according
  to the separate child-safety procedure, and record only the non-content metadata that procedure
  requires. This document deliberately does not define that procedure or name its owners.

If you are changing the moderation code, keep this exclusion in the comments and the tests that
pin it: the model classifies harmful content, and the child-safety control lives elsewhere.

## The four phase-1 surfaces

Phase 1 wires moderation to four surfaces. Three fail open; only publish/egress fails closed.

### Generation prompts

Moderation runs **before any budget is reserved and before any vendor call**, so a blocked prompt
costs nothing:

- `VideoGenerateStepExecutor`, in both the inline and plan paths, before `ReserveAsync` and the
  vendor submit. The prompt text and any reference images are classified.
- `AudioGenerateStepExecutor`, before `ReserveAsync`.
- `ImageGenerateStepExecutor`, before the budget gate (the prompt or edit instruction; a block is `CONTENT_BLOCKED`).
- `EditImageOperationsController`, after prompt sanitization and before the charge hold and the
  vendor call. The source image is the project file's derived frame, never the original.

A block fails the step with `FailureCode = CONTENT_BLOCKED` and `FailureClass = UserInput`, using
the plain reason; `ReserveAsync` and `SubmitAsync` are not called. In the image-operations route
the response is `400 { message, code: "CONTENT_BLOCKED" }` with no hold and no vendor call. A
review-band result allows the action and writes an audit row.

### Ingest

`ContentModerationWorker` scans a project file after every upload, import and cloud-sync commit
(`ProjectFileAddedEffects`, `DirectUploadService.CompleteAsync`, `CloudSyncService`, and the image
editor's export). It handles images and video.

`ProjectFile.ModerationStatus` is a `FileModerationStatus`:

| Value | Meaning |
|---|---|
| `Unmoderated` | Waiting for the worker |
| `Scanning` | The worker is classifying derived frames or the transcript |
| `Clean` | No block |
| `Quarantined` | A high-confidence hit moved the object out of the usable tree |
| `Error` | Moderation could not run; the file stays usable (fail open) |

A block moves the object to
`projects/{projectId}/quarantine/{storageFileName}`, updates its storage key and prefix, marks it
`Quarantined`, stops indexing and summarization, and publishes the indexing delete event.
Quarantine is enforced on every read path, not just labelled: download, content read and update,
media streaming, derivative content, and the assistant's file listing refuse or hide the file.
`List` keeps the row so the UI can report `moderationStatus`. A block also stamps the project's
`content_quarantined_at` marker in the same save, which keeps earlier output of the project from being
reused (see [`csam.md`](csam.md)).

Scanning is asynchronous. The HTTP upload still succeeds (`201`); the file is quarantined after
the response if the scan blocks it.

### Generated text before render and TTS

Generated text is classified before it reaches `drawtext=` or a speech vendor:

- `VideoCompileStepExecutor`: every caption source line after the second-pass sanitize, and
  overlay text and subtext, in one call before the text files are written.
- `VoiceoverStepExecutor`: every sanitized narration line, before the first `SynthesizeAsync`.
- `ThumbnailGenerateStepExecutor`: the sanitized overlay text.
- `TimelineRenderer`: title and caption clip text before the filter graph is built.

A block fails the step with `CONTENT_BLOCKED`/`UserInput` and the plain reason, and the ffmpeg or
TTS call is not made. A review-band result proceeds. The off path is unchanged: with moderation
disabled the generated files and argv are byte-identical to the pre-feature output.

### Publish and egress

This is the only fail-closed surface. Publication routes read a stored verdict; they do not run
the pipeline. Each output is judged once, in the background, when its step completes (see
[Output verdicts](#output-verdicts)). These routes read that verdict:

- output-link mint in `AssistantOutputTools.GetOutput`;
- `OutputsController.DownloadOutput`;
- derivative downloads in `MediaController` whose source is a step result;
- review-link creation (`ReviewLinksController`) and the guest video route
  (`ReviewLinksPublicController`);
- the media files of an edit-timeline hand-off package (`EditTimelinesController`);
- a project file that is a registered output, meaning its storage key is an output key:
  `ProjectFileMediaController`, `ProjectFilesController` download and content read, and derivatives
  of such a file in `MediaController`.

A block refuses the link, download or export. The routes answer `422 CONTENT_BLOCKED`,
`409 CONTENT_MODERATION_UNAVAILABLE` or `409 CONTENT_MODERATION_PENDING` with a plain message and
code. `OutputsController.DownloadOutput` adds `Retry-After: 5` to the pending answer. The
output-link mint returns no link at all (a plain refusal to the assistant), and an anonymous
link or guest fetch keeps its existing bare `404` for any failure.

A pending answer means the output has no verdict for its current bytes yet, because it was just
made, its bytes changed, or it is waiting in the queue. It is refused whatever
`FailClosedAtPublish` says, and the client can retry; the output appears once the verdict lands.
`CONTENT_MODERATION_UNAVAILABLE` is the recorded-outage answer. It refuses while
`FailClosedAtPublish` is `true` (the default); setting it to `false` lets a plain outage through
for an operator who accepts that risk. An answer that cannot be classified always refuses. A
review-band result still publishes and adds a queue row.

An output with nothing to classify (sampled fine but no frames or words, such as wordless music, or
an unsupported type) gets the terminal reason `nothing_to_classify`. It is never retried or sent
again. The output routes refuse it as before; the project-file routes keep serving it, as they did
before verdicts existed.

The refusal body is `{ message, code, final }`. `final` is `true` only when the answer is certain for
these bytes (a file that could not be checked as it is: two audio tracks, a false length, not a playable
video, a video longer than 12 hours, a frame that is not the one asked for) and trying again will not help.
Its message says what to do: re-export the file in a common format and use that. A hand-off package with
media names the clip as well ("The clip \"name\" cannot be put in a package with media ... Re-export it in
a common format and use that instead, or take the package without media."). The words never name a
format, since the file may already be in it. The same words are given on the download of an output the sampler cannot take (a
`.ts` or `.mpg`, which are refused because their seek is not exact). It is `false` for an outage, a pending answer and a block. Clients
read `final`, never the message text. It is present on every refusal the output, file and copy routes
give (`ToRefusal`) and on the review-link answer.

**Generated wordless audio is cleared, narrowly.** Wordless audio is stored as an `Allow` with the reason
code `generated_wordless_audio` (no vendor call; the normal ETag + size binding applies afterwards) only
when all of these hold, each read from stored facts and each failing closed when it cannot be read:

- (a) a Completed `AudioGenerate` step result of the same project made it: its `OutputStorageKey` is the
  key, or its takes manifest (`ArtifactStorageKey`) lists the key as a take it delivered, and the manifest
  kind is `Music` or `SoundEffect`. A row copied from another (`ReusedFromStepResultId`) does not count,
  and neither does a take the step reused or carried from an earlier run: the step that made it counts.
- (b) the manifest take carries `promptCheck` `Allow` or `Review`. The step writes it at the moment the
  prompt is judged on the `GenerationPrompt` surface, only when a real verdict came back. The prompt
  surface fails open, so an outage, no provider or moderation off writes nothing, and nothing means not
  cleared. The audit rows are not read: they expire after `AuditRetentionDays`.
- (c) one audio track and no video (the stream layout decision; two tracks is already unclassified).
- (d) the length measured from the decoded samples, not the declared one, is under `MaxAudioSeconds`
  (within one second of it counts as reaching it), so the whole track was heard.
- (e) the bytes are unchanged since the step. The step records no ETag, so the object's store time
  (`LastModified`) must be no later than the step's `CompletedAt` plus 5 seconds; from the verdict on, the
  ETag binds it. A same-size rewrite after the step is later than that and is not cleared.

Everything else with an empty transcript stays `nothing_to_classify`: uploaded or rewritten audio, other
steps' audio, audio the check could not measure. Speech is judged as usual, and audio with an attached
picture still judges the picture. Audio generated before the `promptCheck` record existed has none, so it
stays `nothing_to_classify` until it is made again, whatever the audit rows say.

**Stays inline.** These are explicit actions, not viewing, so they are judged in the request with
the content-hash audit row or an in-process entry reused for 24 hours:

- image exports (`EditImageAssetsController.CreateExport`);
- text exports and the interchange text of a hand-off package.

A hand-off package that includes media also reads the verdict of each included media file, so
it can answer `409 CONTENT_MODERATION_PENDING`.

### Output verdicts

Each output is judged once, when it is created. The step-completed consumer offers the output to
a single worker lane that handles one output at a time. The result is one row in
`output_moderation_verdicts`, keyed by project and storage key and bound to the object's version
(ETag plus size).

- **No time expiry.** A stored verdict is never re-judged because it is old.
- **Re-judged only if the bytes change.** A changed version reads as pending until it is judged.
- **No automatic re-judge.** Changing `BlockThreshold`, `ReviewThreshold` or the hard-block list
  does not change stored verdicts. Nothing re-judges existing outputs under a new policy unless
  an operator deliberately removes verdict rows.
- **One paced backfill.** A sweep offers up to two outputs at a time for completed outputs that have
  no verdict row, looking back 30 days (an older output that never had a verdict is judged the moment
  anything reads it). It also retries recorded outages with a back-off, up to ten attempts in a row,
  after which the sweep stops and a view may ask again, at most every six hours. A decided verdict is
  never offered by age, and neither is a refusal that is certain for its bytes (see below).
- **Frames and transcript leave at creation.** Each output's sampled frames and transcript go to
  the vendor when it is created, even if nobody ever opens it. The Privacy Policy states this, in the
  purposes section and the OpenAI row of `site/lib/provider-data-handling.ts`.
- **A project under a CSAM hold sends nothing.** The hold is checked before any byte is read and
  before every vendor or transcription call, on this lane and on the inline image export; a failed
  check counts as held. See [CSAM exclusion and runbook](#csam-exclusion-and-runbook).
- **A held output asks for the CSAM scan at once.** With the CSAM control on, a project file whose scan is
  still `Pending` or `Scanning` holds every output of its project (sampled frames could include it). The
  engine registers renders, generated takes and agent files as project files that start `Pending`, so a
  run holds its own new renders. The lane no longer waits for the CSAM sweep's interval (15 minutes, which
  is how every new render read as pending for 15 to 30 minutes, longer while the run kept writing files):
  a held output asks the CSAM sweeper to scan that project's pending rows now (`ICsamScanRequests`, one
  waiting request per project, served on the sweeper's own task, no one-minute grace). When the scan is
  done, and whenever the interval sweep scanned something, the sweeper wakes this lane's sweep
  (`IOutputModerationWake`), which offers the project's outputs again. A quarantine or the output marker
  is not a pending scan: the request finds nothing to scan and the outputs stay held. Nothing is sent to
  the vendor before the scan has a verdict; only the wait is shorter. Expected wait for a render: the
  CSAM scan (one Arachnid call per pending file, 10 s timeout each) plus the judgement itself.
- **An offer that ended in a row or a hold is not cooled down.** The sweep keeps an output it offered out
  of the next sweeps for 10 minutes only when the run wrote nothing (a missing object, a project that is
  gone). An offer that stored a verdict or an outage is governed by that row (an outage at its own retry
  time: 1 minute after the first, not 10), and a held offer is offered again as soon as the hold lifts.
- **Earlier judgements are reused only when they are a Block.** The backfill reuses an audit row only
  for the same bytes, and only a Block: an older Allow or Review came from the earlier, partial sampling
  (a 40 s window, a first frame) and is not good enough to be permanent, so the output is judged once
  more by the current pipeline.
- **Bytes cannot leave the gate with a pending or blocked verdict.** Moving a generated output to
  another location, renaming a folder that holds one, saving it as a file, or placing it as a picture
  layer is refused unless it has a current Allow or Review verdict for its present bytes. Pending,
  changed bytes, a block and a recorded outage all refuse, whatever the fail-closed option says, with
  the same codes and statuses as download. An output with nothing to classify (wordless audio) can
  still be moved or copied. A move that keeps the location, or a rename that changes only the
  displayed name, leaves the output where the gate still applies.
- **The file's type is read from its name.** The extension decides how an output is judged. The
  content type stored on a project file is only a hint for a name with an unknown extension, and
  never sends media down the text path. If the stored type contradicts the name (text on a video,
  video on an image), the output is recorded as unclassified and refuses; it is not treated as
  having nothing to classify. Bytes in a text output must be valid text: a NUL byte or invalid
  UTF-8 is unclassified. The content type of a generated output cannot be changed through the file
  content route (409, `OUTPUT_CONTENT_TYPE_LOCKED`); new bytes there read as a new version and are
  judged again.

- **A failure is never an allow.** A video whose frames were only partly sampled, whose streams could
  not be read, whose length could not be measured, whose audio track could not be extracted, or whose
  transcription failed is recorded as unclassified; it is never judged on what is left. The sampler
  returns the frames it did get together with the failure: at ingest those frames are still judged (a
  Block still quarantines) but the file is not called Clean; on the output lane the output is refused.
  A video with no audio stream is judged on its frames.
- **One rule for what a file is.** How a file is sampled is decided once, from its streams and container,
  never from its name or declared type. Only a layout that is positively identified is sampled: (1) an
  image (an image container, one video stream, no audio, no attached picture; still or animated), (2) a
  video (exactly one real video stream, at most one audio stream; frames are taken from that stream, not
  from a cover), (3) audio (no real video stream, exactly one audio stream). In a video or audio file each
  attached picture (a cover or thumbnail) is judged too, up to four; more than four, or one that cannot be
  read, fails. Anything else fails closed and certainly: several video streams, several audio streams, an
  image container with audio, a container that is not identified, a stream report that cannot be read. Data
  and subtitle streams do not change the layout; their text is read and judged (see Subtitles).
- **Never by time.** An output is checked once, when it is created, and again only if its bytes change.
  A refusal that is certain for its bytes (more than one audio track or video stream, a declared length
  that does not match the video, a type the sampler does not take) is stored with a "never" retry time:
  nothing samples it again by time, no view offers it, and its message does not promise that trying again
  helps. Only a real outage is retried, with the back-off above and, once that stops, by a view at most
  every six hours.
- **Speech that cannot be heard.** A video or audio file with sound needs a **platform** transcription
  provider. Without one the file is refused as an outage (retried later), never stored as an Allow on its
  frames. `ContentModeration:AllowFramesOnlyWithoutTranscription` (default `false`) lets an operator choose
  frames-only deliberately; the stored verdict then carries the reason `frames_only`. Production must have a
  platform transcription provider configured; the flag is for development and self-hosting. A file that
  has an embedded cover picture (an audio file with artwork) has the picture judged too.
- **Streams decide how a file is sampled.** Video inside an audio or image container is sampled as
  video, and an animated image is sampled as video, never on its first frame alone.
- **Frames cover the whole video.** At most 20 frames are taken, at evenly spaced times over the
  measured length (the spacing grows with length, never below the configured interval). Each frame is
  one bounded seek to a keyframe, so a long video costs no more than a short one, with a time bound for
  the whole output; a frame that cannot be taken is a failure, not a wait. An animated image (GIF, APNG) is
  sampled by evenly spaced decoded frames up to the same cap, so a 1.5 s GIF gives every frame it has.
  Animated WebP is refused because ffmpeg cannot decode it. A JPEG is opened by its content, never by
  its name. Audio is transcribed up to `MaxAudioSeconds` (600 s by default): speech after the first ten
  minutes is not heard by the check.
- **Nothing to classify is only for wordless audio.** Audio that sampled fine with a real empty
  transcript is final and may be served and moved (generated music). A video with no frames, an
  unsupported type or an unknown extension is unclassified and refuses everywhere. Text extensions
  such as `.csv`, `.edl`, `.xml` and `.srt` take the text path.
- **Every route that serves or copies the bytes uses the stored verdict.** Download, review links,
  output links (checked at each fetch by the very rule the link is minted with, with the same uniform
  not-found), file routes, picture layers, reference pictures sent to an image-edit vendor, voice
  cloning recordings, brief files, segmentation, the assistant's line, outline and edit tools,
  and picture creation from a file, looking at an output with the assistant, reading a text output
  with the assistant, saving as a file, and moving. A picture that loses its original file keeps a
  placeholder instead of a copy of an output that has no current verdict, and the delete goes on. A
  render preview is refused when a source clip is blocked. The account export packs a generated
  output only if it was cleared, and the manifest states how many were left out.
- **Text sent to the vendor honours the CSAM hold too.** Text exports and hand-off text of a held
  project, or when the check fails, are refused without a vendor call.
- **One organization cannot fill the lane.** Each organization may have a handful of offers waiting
  (5); the rest are left to the sweep, which takes one output per organization in turn. The gauge
  `reelbolt.moderation.output_queue_depth` reports how many offers wait in or run on the lane.
- **Verdict rows follow the object.** A row is removed when its object is deleted. When an object is
  moved, its released verdict (allowed, reviewed or wordless) moves with it, so unchanged bytes are not
  judged again, but only if the copy was conditional on the ETag that was judged and the ETag and size
  read at the new key both equal the judged ones; any difference, or a store that cannot condition the
  copy, drops the row and the new key reads pending. A storage failure part-way through a move leaves each
  row on the key its object really has, and the files that did move are still asked to be indexed.
- **No ingest status becomes an allow.** An uploaded file that ingest moderation quarantined stays
  refused. Nothing else is read from the ingest status (it records neither which bytes it judged nor
  under which rules): any other uploaded clip is offered to the output lane and reads pending until it
  has a verdict row. A hand-off offers every pending clip in one go.
- **No preview before a verdict.** A render preview, its price estimate and the playback of one are refused
  while any source clip that is an output lacks a current verdict; a timeline that cannot be read is
  refused. Playback checks the clips of the revision the preview was rendered from, not the current
  timeline; a timeline that is gone, or a revision that can no longer be read back, refuses.
- **The sweep is paced and fair.** Organizations take turns, the one served longest ago (or never) first,
  across every organization that has something waiting (due retries first, then the newest never-judged
  outputs, a few per organization); a view's offers are capped per organization, so one tenant's activity
  never stops the sweep for the others. An idle sweep waits longer each time, up to
  `ContentModeration:MaxIdleSweepSeconds` (60 s by default, 10 minutes at most), returning to 30 seconds when
  it finds work or an output was produced in the last few minutes; a wake (a CSAM scan finished) ends the
  wait at once. A decided output is never offered by age. On a throwaway database with
  200,000 step results the idle query read about 40,000 buffers (about 100 ms) with the 30-day look-back,
  against about 360,000 (about 300 ms) without it.
- **Real duration and frame count.** The declared duration is not trusted. A video's packets are listed
  with `ffprobe` (no decoding; 1.4 s for a 2 h, 30 fps file) and its length comes from that list, not from
  log text. A video whose declaration is far from the real length, or that cannot be measured, is
  unclassified; an image with more than one decoded frame is sampled as video (by decoding); an image that
  cannot be measured is unclassified; more than one video stream is unclassified. The comparison is between
  the video stream's measured length and the length that stream itself declares, not the container's, so a
  short video with a longer audio track is not a mismatch.
- **Frames are chosen at random, half by time and half by packet position.** The frames to judge never
  come from the clock alone, so a one-line remux that moves the last frames an hour later cannot make every
  sample hit one frame, and never from packet position alone, so thousands of tiny packets cannot crowd out
  a long still. At most 20 frames are judged (one per 2 s for a short video). First the packets that stay
  on screen for longer than 60 s (or longer than one slice of the length, when that is shorter) are always
  judged, decided on their real time on screen and never on a capped weight: the longest first, up to half
  the frames. Past that half the other long holds are not lost: they stay in the slices below and are reached
  in proportion to the time they fill. The held packets are then taken out. Of the rest, half are the frame
  on screen at a random point inside each of equal slices of the played time that remains (real time: a
  video longer than 12 hours is refused, so a still cannot be padded to a day to move the odds), and half are
  one packet drawn at random inside each of equal strata of the positions of the rest, so thousands of tiny
  packets are reached in proportion to how many they are. A frame drawn twice is judged once and its place
  is drawn again. No time is predictable and a changed file is drawn afresh. Fewer packets than frames are
  all judged. **The last frame counts up to the end of the longest stream.** A video whose last frame stays
  on screen while a longer audio track plays (10 s of video over 1 h of audio) has that frame as a hold of
  the rest of the audio, in the weights, in the holds and in the 12-hour limit. **The end of the audio is read from the audio
  stream's own last packet (its time plus its duration, from `ffprobe` packets, bounded like the video list), never from a length the
  container declares:** a live webm declares none and another a low one, and both used to hide the red last frame. A video-only file
  with a lying container length is not extended. Reach of the one red packet,
  400 draws each with the real `PickPackets` (the table is printed by `ModerationMediaSamplerPickTests`):
  `redhold` (10,000 tiny packets, one red still of 10.8 h, 73 blue stills of 59 s; 13.9% when the cap decided
  the holds) 100%; `tail` (300 frames and a red one, 1 h of audio; 2.0% before) 100%; `tailtiny` (10,000
  packets and a red one, 1 h of audio; 1.1% before) 100%; `pos10k` (10,000 tiny packets and 25 red stills of
  59 s) 100%; `gap` (60 s and a last frame an hour later) 100%; `hold59` (a 59 s red still among 200 s) 100%;
  `m12h` (700 blue stills of 59 s, 25 red ones) 59%, a share (3.4% of the time, and half the frames by position among 725 packets), not a guarantee. With real ffmpeg on small files, `redhold.mkv` and `tail.mp4`/`tailtiny.mp4` have the red
  frame in 10 of 10 and 8 of 8 judgements each. A video measured longer than 12 hours, from its packets or from
  the longest stream of its container, is a final refusal
  on the output lane (the number is a product choice, kept in one constant); ingest still judges what was
  sampled. Within the limit a short scene is reached in proportion to the time it plays. A hold is never refused: a screen recording that sat still, slides, a variable frame
  rate recording with a 90 s slide in a 94 s video (also after a stream-copy remux), and a still held over
  long audio all pass with the hold judged. The declared length is compared with the measured one only when
  the video has more than one packet, and with the video's own packets, not the audio's. At most 1,000,000 packets are read; more is refused. A video that is a
  marked colour for 7% of the time in short flashes is reached by a judgement with about 77% probability
  (`1 - 0.93^20`); a file cannot have its flash at a time that is known in advance. The seek is accurate
  (`-ss` before the input, no `-noaccurate_seek`) and counts from the container's start time, so the frame
  returned is the chosen one, not the keyframe before it. A seek that fails on one drawn packet (AVI and FLV
  can) tries the next packet. The frame is written without frame duplication or drop (`-fps_mode passthrough`):
  with the default constant-rate sync, a hold of hours between two packets made ffmpeg write the frame AFTER the
  drawn one (shown on `redhold.mkv`: the red frame's time was printed, the blue frame after it was stored).
  **Packets before the first shown time are not drawn.** A stream-copied mp4 or mov cut in the middle of a
  group of pictures keeps the frames before the cut with negative times; they are never on screen and a seek to
  one returns the first shown frame (shown on `-ss 3.3 -i src.mp4 -c copy`: 58 packets of 417 before 0, and a
  draw landing on one stored a final failure for about three judgements in four). The length of the video is measured from the
  first shown packet too, so a cut shorter than its own pre-roll (a 3 s cut at 7.5 s in an 8 s group, a 1 s cut in a 2 s group) is not
  refused for a declared duration that "does not match".
- **The frame returned is checked, and decoded from the start when it is not the one drawn.** Each frame pass keeps the
  frame's own container time (`-copyts`) and prints it on stdout (the `metadata` filter). The fast seek stays for the normal
  case. When the frame returned is not the packet drawn (its time more than 2 ms off, or nearer another packet, no time
  printed, or no file written) that packet is decoded from the start of the file, ignoring the index (no seek; frames are
  dropped until the one whose time is within 0.5 ms of the packet's), and only that frame is accepted, by its printed time.
  So a keyframe table that lies, an open GOP, or an AVI's seek cannot hide a scene: every sampled frame is the packet drawn,
  or the file is refused. There is no position tolerance, near-miss count, first-packets exemption or neighbour retry.
  **At most 12 such decodes per output** (`MaxSlowDecodes`); more is final, with the frames kept for ingest (a lying index
  mismatches nearly every draw, an honest file a few). A decode from the start that ffmpeg fails, or that finds no frame at
  that time, is final, except an **undecodable lead**: when no frame has that time and the first frame the file decodes at
  all is later, the packet is before anything playable, so it is never shown, is taken out of the drawable set and its place
  is drawn again (at most 4 times in one sampling). A time-out of a decode from the start (40 s, inside the total deadline)
  is NOT final: it is the non-deterministic retry path, never an Allow. Measured on 96x72 files of 2 h: the normal path
  2.4-4.6 s for AVI, FLV, long GOP (60 s) and 2 h low resolution; one decode from the start of the last frame 3.2 s (AVI),
  4.7-5.1 s (FLV, MP4); x265 open GOP at 25 fps (10 s keyframe) 2.7-3.0 s whole, 0-1 decodes from the start; so 12
  fit inside the 120 s deadline for low resolution, while a long high-resolution file that needs them can time out and retries.
  Shown with real ffmpeg: files with a false sync entry on a 10.8 h red still (`xredhold`), three stills (`xred3`), red
  throughout (`xalt_all`, `xalt2`: refused by the cap), first packets, last packet and red blocks of 10-15 percent
  (`xblock*`): the red is reached or the file is refused, in every seed. Scenes built of near misses (`nearb*`) are reached
  exactly as often as the same file with a true index, which is the draw. Honest files pass: x265 open GOP at 2 fps and with 8
  B-frames, H.264 open GOP in fragmented MP4, B-frame AVI, MPEG-4 AVI at 60 fps, MXF, B-pyramid, VFR, FLV, copy cuts with the
  first keyframe at 0.5, 1, 2 and 8 s.
- **Seek-unsafe containers.** MPEG-TS and MPEG-PS return the keyframe for a seek, so a crafted file shows
  the check only its keyframes (shown with real ffmpeg: `kf15.ts`, every frame returned blue where the
  video is red between its keyframes). They are refused on the output lane as a final failure; ingest
  still judges the frames it got. MXF was tested the same way and returns the requested frame, so it is
  sampled like MP4 and Matroska. A raw `.ac3` file (the `ac3` demuxer: one elementary audio stream, it opens no other file or address) is on the format whitelist, so it is sampled as audio and not refused as unreadable.
- **Two video packets with one printed time.** A final refusal on the output lane (the frames are still sampled for ingest): the frame check cannot tell which of the two was drawn. Honest B-frame, open GOP, VFR, fragmented MP4, AVI, FLV, single copy-cut and MXF files do not have such a pair (tested on 6 seeds). The engine's own outputs from the stream-copy path have no pair: a single continuous cut is one copied piece, and a cut with more than one span is re-encoded, so its packets are new. A cut made by hand (cut the pieces, then concatenate them in an upload) can have one when the source has B-frames, and is refused here.
- **Two or more channels are heard whole and channel by channel.** Mono is one transcription. Stereo is heard as
  the sum (each side at 1/2) and as left minus right, which is extracted and transcribed only when its loudest
  1-second window is louder than -50 dB (a mono-compatible file costs one transcription). Audio of more than two
  channels, up to 8 (the engine's own 5.1 outputs included), is heard as the full mix of every channel (each at
  1/n, one transcription) and each channel on its own, by its index (no layout name is read): every channel whose
  loudest 1-second window (`astats length=1`, one measuring pass) is louder than -50 dB is extracted (16 kHz mono,
  the same range and byte bound) and transcribed, so speech spread across channels in time is heard whole and speech
  on one channel, or in opposite phase between two, is heard alone. The number of transcriptions is 1 plus the
  number of audible channels (at most 9; a silent channel costs none); a layout the ffmpeg pan cannot take fails the
  extraction and is a failed audio pass, not an Allow. Two refusals, both final (the frames are still kept for
  ingest): more than 8 channels, and any change of the audio channel count (stereo that turns 5.1, 5.1 that turns
  stereo, mono that turns stereo, in every layout). The change is read from the frames the decoder produces
  (`ffprobe -show_entries frame=channels`, no stream-header probe alone, one decoding pass of at most the listening
  range per audio file), and the channel meter keeps the loudest reading of each channel across a layout reset, so
  a reset cannot lower one. Accepted limit: in an audio-only MPEG-TS or MPEG-PS file, a channel change that follows a jump in the stream's time
  is not seen; other containers, and any file with a picture, are checked as above.
- **Audio heard for less than declared.** The decoded audio is compared with the length it declares
  (the audio stream's own, else the container's), capped at the listening limit. Less than three quarters
  of that, and more than 1.5 s short, is a final failure (a chained Ogg that is heard for 5 s of 33 s).
- **Subtitles.** The text of text subtitle streams (SubRip, ASS/SSA, WebVTT, mov_text and the like, up to 8
  streams and 1 MiB of text) is read, stripped of cue numbers and time lines (only a line that is
  exactly a timing line, `00:00:01,000 --> 00:00:02,000`, optionally with real cue settings with real values
  (`align:start|center|middle|end|left|right`, `line:` a number or percentage, `position:` and `size:` a
  percentage, `vertical:rl|lr`, the SubRip coordinates `X1:` to `Y2:` as digits), is dropped; a timing followed
  by any other word, or a setting with another value (`line:BUY size:DRUGS`, `region:` whose id is free text),
  is kept whole, as is a line of words with an arrow in it; a line of digits only is dropped only when the
  next line is a timing line, so a phone number in a cue is kept),
  and judged with the transcript as the same words. A bitmap subtitle stream, one in a format not known to be text, one that cannot be
  read, or one over the text bound is a final failure. Data streams stay ignored: nothing plays them.
- **Threads.** Every ffmpeg pass that decodes runs with `-threads 2` (one crafted file used every core of the
  host). Measured on synthetic media (640x360, 30 fps, three runs, whole sample end to end, unlimited
  threads against two): 30 s 2.0-2.5 s against 1.8-2.0 s; 5 min 3.2-3.6 s against 2.6-3.4 s; 2 h 7.8-8.5 s
  against 7.6-9.8 s; 5 min with a 100 s GOP 8.0-9.0 s against 8.6-9.5 s. The cost is within the run-to-run
  spread; the frame decode on the 100 s GOP file used about 3.8 cores unlimited and about 1.7 with the limit.
- **A name that tells nothing.** A stored output with no extension, `.bin` (which the audio step writes when a
  provider names no type) or any extension the engine does not know is sampled by its streams, not its name:
  real media is judged, and wordless generated audio can clear. A file with no media in it is refused. A
  `.ts` still refuses, by its demuxer.
- **A host fault is not final.** A full disk or a quota error from ffmpeg is a retryable failure, never stored
  as a refusal that is certain for the bytes. The words are read only from ffmpeg's own error lines: the
  metadata ffmpeg prints about the file is indented, and a line that starts with white space is never read, so a
  file titled with those words stays final.
- **One total deadline.** All passes for one output run under one deadline of 120 s. A real 2 h, 30 fps file
  with 10 minutes of audio takes 11 s end to end (1.4 s of it the packet list), so the deadline is over 10
  times a long real file; the sum of the
  per-pass limits (about 7 minutes) is what one crafted file could hold the lane for before. Past the
  deadline a pass is refused as a timeout, which is a retryable failure (not final), retried a bounded
  number of times.
- **Which failures retry.** A timeout, a kill by a signal (exit 129-159, including the out-of-memory kill,
  137) and ffmpeg's own out-of-memory exit (244) say nothing about the bytes and are retried. A clean ffmpeg
  error stays final. An attached picture that is a GIF or WebP is refused (it can be animated and a cover is
  judged on one frame).

The row stores the state, reason code and message, the model snapshot, attempt count, next
attempt time, the time the output was produced and the time of the verdict. It holds no content
and no hash, and it is removed with the object. A row can briefly outlive an object removed by the workflow engine itself; it holds no content.

To read verdict latency (seconds from an output being produced to the row's first answer), run this
read-only query. It covers the last 24 hours and excludes backfill, as the metric does. It uses
`created_at`, which is set once when the row is first written; `decided_at` moves on every retry, so it
would measure the latest attempt, not the first answer:

```sql
SELECT
  percentile_cont(0.5) WITHIN GROUP (ORDER BY secs) AS p50_seconds,
  percentile_cont(0.9) WITHIN GROUP (ORDER BY secs) AS p90_seconds,
  count(*) AS verdicts
FROM (
  SELECT EXTRACT(EPOCH FROM (created_at - output_produced_at)) AS secs
  FROM output_moderation_verdicts
  WHERE created_at >= now() - interval '24 hours'
    AND output_produced_at IS NOT NULL
    AND created_at - output_produced_at BETWEEN interval '0' AND interval '1 hour'
) t;
```

The same figure is exported as the histogram `reelbolt.moderation.output_verdict_seconds` on the
meter `ReelBolt.Inference.Moderation`.

## Library copies follow the source's verdict

A workspace avatar library copy is judged by the moderation verdict of the files it was made from: while any project
file of the same organization with the same SHA-256 is moderation-quarantined, the copy is not served (`404`), shows as
unusable in the library list and cannot be imported. The check runs on every read, and a durable marker
(`held_at` / `held_reason` `moderation` on the library copy) is written the first time it holds and in the same save
that removes a quarantined source file, so deleting the source does not release the copy.

## Policy and thresholds

The policy reads the **highest category score** in the response and applies the configured
thresholds:

| Highest score | Action |
|---|---|
| `>= 0.85` (`BlockThreshold`) | Block |
| `>= 0.50` and `< 0.85` (`ReviewThreshold`) | Review — allowed, audit row only |
| `< 0.50` | Allow |

Two rules override the bands:

- **Hard-block categories.** Any effective hard-block category that is flagged, or scores at or
  above the review threshold, blocks regardless of the top score. The configured
  `ContentModeration:HardBlockCategories` list replaces the default; when it is empty the
  effective list is `sexual/minors`. That category is a policy signal, not a CSAM control — see
  [CSAM exclusion and runbook](#csam-exclusion-and-runbook).
- **A flag with no high score is a review, never an allow.** If the vendor returns
  `flagged: true` but no score reaches our thresholds, the action is Review, not Allow.

An **unclassified** response is never clean. An empty input, an empty `results[]`, fewer
`results[]` entries than inputs, and a response with neither scores nor flags are all
`Unavailable`; the client throws for the malformed shapes and the policy returns
`ModerationAction.Unavailable` for a scoreless answer. A scoreless answer still writes an audit
row with `Action = Unavailable`, never `Allow`; a thrown call takes the surface's failure stance
and is logged. The surface's stance decides what happens next.

**A review blocks nobody.** It writes one audit row with `ReviewState = Pending` and the original
action proceeds. Reviewing or acknowledging it in the admin queue changes no user-visible state.

User-facing text is a plain reason that names the surface, for example "This image may contain
sexual content." or "This content may contain hateful content." It never contains a score, a raw
category id or the model name. Keep `ModerationReasons` as the single source for those sentences.

### Recalibration

The thresholds are **policy choices, not measured performance**. The vendor's guidance is to
treat category scores as signals, and the `-latest` alias can move to a new model under the same
API. Pin the snapshot in `ContentModeration:Model` and recalibrate when it changes:

1. Collect a labelled sample from the review queue and the audit rows (scores and categories, not
   content) alongside normal operations.
2. Compare the block and review bands against that sample. A growing review band is usually the
   safer tuning direction; lowering the block threshold without a review path is not.
3. Change `BlockThreshold` and `ReviewThreshold` together, keep `ReviewThreshold <= BlockThreshold`,
   and keep the hard-block list explicit.
4. Re-run the moderation test suite. Findings are policy changes, so record what changed and why.

The policy clamps the thresholds to `[0,1]`, never lets review exceed block, and falls back to
the shipped defaults when a configured value is non-positive.

## Failure stance

Moderation is one short call per surface. A failed call must not stall a step or an upload.

| Surface | On timeout, unavailable, no provider or malformed response | Stance |
|---|---|---|
| Generation prompt | The step proceeds and may reserve and submit | **Fail open** |
| Ingest | The file stays usable and is marked `Error` | **Fail open** |
| Generated text | Render or TTS proceeds | **Fail open** |
| Publish / egress | No link, download or export | **Fail closed** |

An output that has no verdict yet is refused as pending until the background worker judges it, and
a recorded `Unavailable` verdict is retried by the worker with a back-off. When the vendor call
succeeds the output publishes with no manual intervention.

Details that follow from the stance:

- One `ContentModeration:TimeoutSeconds` timeout per request, default `8`, clamped to `1..30`.
  Each input (one image, or the transcript) is its own request, at most four in flight, so a
  20-frame video is a handful of round trips. The client uses its own short timeout, not the
  provider row's general generation timeout.
- A caller cancellation (a stopped upload or run) is re-thrown and is not treated as a moderation
  outage. Only an actual timeout or vendor failure takes the stance above.
- A failed usage or audit write is logged loudly and swallowed: the moderation decision already
  happened, and a ledger/database hiccup must not turn a fail-open surface into a failed step or
  upload. The audit row is still the security record when it lands; a lost one is an operational
  alert, not a content decision.
- The fail-open surfaces log the gap. When the vendor answered but returned no usable signal, the
  audit row records `Action = Unavailable`; a timeout or vendor error is logged without a row.
  Neither case silently claims a clean result.
- Process hygiene is internal hardening, not an operator policy: error bodies are read to 4 KB,
  success bodies to 1 MB, ffmpeg is killed as a process tree on timeout or cancellation, and the
  extracted audio is duration- and byte-capped (see [Operator configuration](#operator-configuration)).
- A non-success response surfaces **only structured vocabulary**: HTTP status, `error.type`,
  `error.code` and `error.param`. They name almost every real cause (`invalid_value`,
  `invalid_image`, `unsupported_value`). Vendor free text — `error.message`, a gateway's top-level
  `message`/`detail`/`error_description`, or `error` as a string — is **never** echoed: it becomes
  `message omitted (N chars)` (the first message-bearing field only; there is no sibling fallback).
  A vendor 400 can reflect the request, and plain prose is indistinguishable from a genuine vendor
  sentence by shape, so free text is dropped rather than filtered. The structured fields are
  redacted before the whole snippet is capped to 300 characters. A non-JSON body, or a JSON body
  with no recognised field, is reported by kind (`(non-JSON error body)`, `(unrecognised error
  body)`).

## Video, images and data handling

The vendor model accepts **text and images only** — no audio, no video. ReelBolt samples before
it calls:

- **Video:** one frame every `VideoFrameIntervalSeconds` (`2`), capped at
  `VideoMaxFrames` (`20`), plus the ASR transcript. An audio-only source uses the transcript
  alone. The original video is never sent.
- **Images:** downscaled to a JPEG whose long side is at most `SampleImageMaxLongSide` (`512`) and
  at most `MaxImageBytes` (`20 MiB`). Video frames go through the same downscale. A larger
  original is never uploaded as-is.
- **Audio:** never sent to the moderation model. The worker extracts a 16 kHz mono WAV for
  transcription, bounded by `MaxAudioSeconds` (`600`) and `MaxAudioBytes` (`20 MiB`); only the
  resulting transcript text is classified.
- **Text:** up to `MaxTextChars` (`8000`) characters per call; longer text is truncated.

Images are always sent **one per request**, and never with text: the vendor accepts an image only
as the single input of its own request, so every sampled frame is its own image-only call and the
transcript is its own text-only call. The verdicts are unioned, so every frame and the transcript
are still classified. At most four calls are in flight. There is no multi-image attempt and no
retry with a smaller shape: any rejection of a single-input request surfaces as
`CONTENT_MODERATION_UNAVAILABLE`.

**Originals never leave the host.** Only downscaled derived frames, transcript text and prompt
text go to the vendor. This is a data-egress decision as much as a cost one: the API is free, so
the real costs are latency, rate limits and sending user media to a third party.

The audit table (`content_moderation_decisions`) is **hash-only**. It stores the SHA-256 of the
classified content and its length, the category flags and scores, the applied input types, the
action, the model and pinned snapshot, the decision id, the object version the verdict was bound
to, and the organization/project/user/execution attribution. It never stores the raw prompt,
caption, transcript, image or video.

The verdict table (`output_moderation_verdicts`) is separate. Its rows have no expiry, hold no
content and are removed with the object. The 30-day audit retention below does not apply to them.
`ReasonMessage` is the plain user sentence and is also score-free. Retention is
`AuditRetentionDays`, default `30`: each row gets `ExpiresAt`, and a background service deletes
expired rows in batches.

## Admin review queue

The review queue is read-only apart from acknowledging a row. It is **platform-admin only**.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/moderation/review-queue?state=pending&limit=100` | `state` is `pending` (default) or `all`; `limit` defaults to 100 and is clamped to 1..500. Returns newest first with the total. |
| `POST` | `/api/v1/moderation/review-queue/{id}/acknowledge` | Moves a `Pending` row to `Acknowledged`; idempotent. A `NotRequired` row is refused with `400`. |
| `GET` | `/api/v1/moderation/settings` | The effective, read-only moderation configuration. |

The page is `/app/admin/moderation` in the web app. It shows created time, surface, project,
content kind, top category and score, action and review state, with an empty state when there is
nothing pending. Its banner says: "Review only — acknowledging records that you have seen this
item. It changes no user content."

**Acknowledging changes no user-visible state.** It writes only the review state and reviewer
columns on the audit row; no project, file or execution is read or written. The queue never
blocks publication or a step.

The route is `/api/v1/moderation/*`, deliberately **not** `/api/v1/admin/*`: nginx sends that
prefix to the Go API. See [`docs/design/model-catalog.md`](design/model-catalog.md) for the
provider tree and `CLAUDE.md` for the endpoint list.

## Operator configuration

The section is `ContentModeration`; as environment variables the separator doubles, for example
`ContentModeration__Enabled`.

| Key | Default | Meaning |
|---|---|---|
| `ContentModeration:Enabled` | `false` | Opt-in switch. `false` = moderation off and every surface proceeds. Turning it on without a provider makes publish fail closed. |
| `ContentModeration:Model` | `omni-moderation-2024-09-26` | The pinned snapshot sent in the request and recorded on the audit row. |
| `ContentModeration:TimeoutSeconds` | `8` | One short call per surface; clamped to 1..30. |
| `ContentModeration:BlockThreshold` | `0.85` | Highest score at or above this blocks. |
| `ContentModeration:ReviewThreshold` | `0.50` | Highest score at or above this (and below block) is a review. |
| `ContentModeration:HardBlockCategories` | `[]` | Categories that block regardless of top score. Empty means the effective list is `sexual/minors`. |
| `ContentModeration:MaxTextChars` | `8000` | Characters classified per call; longer text is truncated. |
| `ContentModeration:MaxImagesPerRequest` | `20` | Images attached to one call; further frames are dropped. |
| `ContentModeration:MaxImageBytes` | `20971520` (20 MiB) | Per-image ceiling; an oversized image is refused. |
| `ContentModeration:MaxAudioBytes` | `20971520` (20 MiB) | Ceiling on the WAV extracted for transcription. Audio is never sent to the model. |
| `ContentModeration:MaxAudioSeconds` | `600` | Seconds of audio extracted for transcription. |
| `ContentModeration:SampleImageMaxLongSide` | `512` | Long side of a sampled still or video frame before encoding. |
| `ContentModeration:VideoFrameIntervalSeconds` | `2` | The least spacing between sampled frames, in seconds; values below 1 are treated as 1. |
| `ContentModeration:AllowFramesOnlyWithoutTranscription` | `false` | Allow a file with sound to be judged on its frames alone when no platform transcription provider answers. Off by default: such a file is refused. Production needs a platform transcription provider instead. |
| `ContentModeration:VideoMaxFrames` | `20` | Frame ceiling; clamped to 1..20. |
| `ContentModeration:AuditRetentionDays` | `30` | Audit rows older than this are deleted. |
| `ContentModeration:UnavailableCacheSeconds` | `60` | How long an `Unavailable` outcome is reused for one object before it is sampled again (0..3600; `0` disables the negative cache). Keeps a provider outage from re-sampling every `Range` request while recovering quickly. |
| `ContentModeration:FailClosedAtPublish` | `true` | Refuse egress when moderation is unavailable. `false` restores fail-open at publish and is an explicit risk acceptance. |

Configure the platform default with the `PlatformProviders` array. Each entry is one capability;
the seeder passes it through the same validation as the admin API and never logs key material:

```bash
PlatformProviders__0__Name=OpenAI moderation
PlatformProviders__0__Kind=OpenAICompatible
PlatformProviders__0__Capability=ContentModeration
PlatformProviders__0__Endpoint=https://api.openai.com/v1
PlatformProviders__0__Model=omni-moderation-latest
PlatformProviders__0__ApiKey=<key>
PlatformProviders__0__IsDefault=true
```

`Enabled` (default `true`) and `TimeoutSeconds` are also accepted. An `OpenAICompatible` entry
takes the key itself, not the `env:` ambient-credential sentinel; a blank `Name` leaves the slot
unconfigured. The row's model code is the catalog alias `omni-moderation-latest`, while the
client sends the pinned `ContentModeration:Model` snapshot.

The price row is created automatically from the price book when the capability is created:
`unit = ModerationCalls`, `cost_usd_per_unit = 0`. Do not replace it with an unpriced row. One
usage event is still recorded per call, so the integration is visible in the ledger and in rate
limit accounting at zero cost.
