# fal.ai models for the thumbnail editor and for video generation - verified survey

- **Date:** 2026-10-10 for the image survey (sections 1-9 and the cross-cutting facts); **2026-10-16** for
  video generation (section 10) and for the queue-shape measurements quoted there; **2026-10-18** for
  `fal-ai/sam-3-1/video-rle` (section 1), read during the sibling screen-insert-occlusion trial pass.
  The passes are dated separately because fal's catalogue moves: a price or an id read on the earlier
  date is not evidence for the later one.
- **Rule:** nothing here is inferred; every row has a source URL. Where the schema does not state something (mask polarity, coordinate space, output size) it says so under "Gaps" instead of guessing. Property tables are generated mechanically from fal's own OpenAPI documents; descriptions are copied verbatim with whitespace collapsed (including fal's own typos and copy-pasted descriptions, which are called out in "Gaps").
- **Queue contract (already known, not re-derived):** `POST https://queue.fal.run/{id}` with `Authorization: Key <FAL_KEY>`; then `GET` the returned `status_url` until `COMPLETED`; then `GET` `response_url`. Billing is per successful output.

## Must measure with a real key (consolidated)

**ImageGenerate (workflow step) made no live fal call.** The `ImageGenerate` workflow step reuses the existing
`ImageGeneration` and `ImageEdit` clients and models listed in this page; its arm was built and tested without any
fal.ai request. Everything the step relies on from fal (prices, output sizes, accepted inputs, multi-picture output) keeps
the assumptions and "Must measure" rows below until one run with the staging fal.ai provider measures them.

Nothing below can be settled from a schema, and no fal.ai credential exists in any deployment, so the client
(`FalAiImageClient`, `FalAiAnalysisClient`, `FalAiVideoGenerationClient`, `FalAiAudioGenerationClient`) ships with the **assumption**
stated here and a test pinning it. Run these once with the staging fal.ai provider (one provider per
account, holding a `Segmentation` model plus one model per picture capability — the **Set up all picture
tools** button on the provider adds them under it, with no key copy — plus one `VideoGeneration` model per
endpoint id you want to try, whose model code is exactly one of section 10's ids) and write the finding
back here; a wrong assumption on the first four rows produces a wrong image,
not an error, and a wrong one on rows 21-27 produces a wrong clip or an unexpected charge.

| # | Question | What the client assumes today | How to measure |
|---|---|---|---|
| 1 | **SAM 3.1 point/box coordinate space** (`point_prompts`, `box_prompts`: integers, space not stated) | Source-image **pixels**, passed through verbatim (`FalAiAnalysisClient.BuildSam3Body`) | Click a known pixel on a 1000x500 and a 2000x1000 version of one picture; the mask should land on the same object if the space is pixels, on a different one if it is a normalized grid |
| 2 | **SAM 3.1 omitting `prompt` with only clicks/boxes** (schema default is `"wheel"`) | The field is omitted; the vendor default is assumed not to override the clicks | Send one point on a non-wheel object with no `prompt`; if it segments a wheel, send `""` instead |
| 3 | **SAM 3.1 `image` with `apply_mask: true`**: RGBA cut-out or a mask visualisation? And `masks[]` polarity | `image` is the first layer's bitmap, `masks[]` the matte (white = keep); masks 2..n use the source picture as the bitmap | Fetch both for one prompt and inspect alpha and the white/black meaning |
| 4 | **Mask polarity of `fal-ai/flux-pro/v1/fill`** (NOT STATED; also `iclight-v2` `mask_image_url`, `bria/fibo-edit/edit` `mask_url`, `openai/gpt-image-2.5/*/edit` `mask_url`) | The editor's mask (white = act on) is sent as-is to `flux-pro/v1/fill` | Fill a known white rectangle with a prompt and see whether the inside or the outside changed |
| 5 | **`fal-ai/lama` and every endpoint whose page says "$0 per compute second"** | Not used; unpriced and never a default | Run one call and read the invoice line |
| 6 | **Probe**: MEASURED 2026-10-10 — an empty-body POST is ACCEPTED at submit (200 `IN_QUEUE`) and validated only when it runs; the probe now follows the job to its result (400/422 = ok). Still to check: that a validation-failed job costs nothing | Yes (`FalQueueClient.ProbeAsync`) | Check the fal usage page shows no charge for a probe |
| 7 | Output sizes the schemas do not state: `ideogram/v4.5`, `ideogram/v3/generate-transparent`, `topaz/*`, `fal-ai/image-apps-v2/relighting` ("4K"), `iclight-v2` with no `image_size`, GPT Image 2.5 `auto` | The size is read from the image header, never assumed | Compare the delivered size with the input for each |
| 8 | Whether `nano-banana-2.1/edit`, `flux-pro/v1/erase`, `object-removal/mask` and the others keep the input's dimensions | Not assumed: the layer takes the delivered size | Compare |
| 9 | Whether `fal-ai/ideogram/v3/generate-transparent` returns a PNG with an alpha channel | Assumed (the output `File` states no content type) | Fetch one and inspect |
| 10 | Edge quality: hair on `bria/background/replace`, over-erosion of small subjects by `object-removal/mask`'s `mask_expansion` default 15, seams of `flux-2-pro/outpaint`, text fidelity of `ideogram/v4.5` at the thumbnail font sizes | Defaults left alone | Visual comparison on a real thumbnail |
| 11 | `fal-ai/image-editing/youtube-thumbnails` visual treatment (the schema describes none); `bria/fibo-edit/*` behaviour | Offered as named presets/alternatives only | Run one |
| 12 | Whether `topaz/upscale/image/precision` already-1280x720 thumbnails and `seedvr` bill input or output megapixels | Card is the first-unit "from" figure | Compare invoice with output megapixels |
| 13 | Seedream 5 Pro: the 2x disagreement between its page sentence and its gallery listing | The higher figure is the card | Invoice |
| 14 | The effective FLUX.3 price after the launch discount that the page says ended October 8 | $0.048 per 1 MP image (the post-discount figure) | Invoice |
| 15 | Token-priced endpoints (`nano-banana-2.1`, `gpt-image-2.5`): real per-call cost including thinking tokens | The page's illustrative table, stated as a floor | Invoice |
| 16 | Whether any edit endpoint returns an alpha channel for an RGBA (cut-out) input. **MEASURED 2026-10-10 for `google/nano-banana-2.1/edit`: no** - the 5931x3954 RGBA car cut-out came back as a 1264x843 PNG, colour type 2 (RGB), no `tRNS`, the transparent area black | Never assumed: the editor carries the source's alpha over to every pixel edit (`PngAlpha`, `docs/editor.md` "Transparency survives an edit") | Inspect the IHDR colour type of each endpoint's answer for an RGBA input; an endpoint that keeps alpha needs nothing, the re-application is a no-op on it |
| 17 | Whether `google/nano-banana-2.1/edit` keeps the input's aspect ratio. **MEASURED once: yes** (5931x3954 in, 1264x843 out, 1.4999 vs 1.4995) | The alpha is carried over only when the aspect matches within 2%; otherwise the user is told | Repeat for the other edit endpoints |
| 18 | **The 9 s `flux-2-pro/outpaint` failure on staging** (5931x3954 photo, 2048 px top and bottom): the vendor's exact message was never seen (Cloudflare replaced the 502 body; the API did not log it - both fixed). Reasoned cause: the requested output was 5931x8050 = 47.7 MP against FLUX.2's documented 4 MP output ceiling (and a 23 MP input over its documented 4 MP input, which BFL "automatically resizes" by an unstated rule) | The planner keeps every FLUX.2 request at or under 4,000,000 pixels in and out, sending a copy at the layer's displayed size when the original is over | Re-run the same expand once on a key; the failure is now logged server-side with the vendor's words |
| 19 | `fal-ai/bria/expand`: output size and placement are taken from the request (`canvas_size`, `original_image_size`, `original_image_location`); not yet seen on a real call. Also whether Bria accepts a 17 MB, 23 MP original (no input limit is documented) | The result is placed from the size actually delivered, so a vendor that rounds still lines up | Expand one large photo to 9:16 and compare the delivered size with `canvas_size` |
| 20 | Whether fal/Bria/BFL apply a JPEG's EXIF orientation to an input. The editor plans in the size the browser SHOWS (orientation applied); a sideways phone JPEG read unrotated by the vendor would come back turned | Orientation applied (as the browser does); a copy rendered by the browser has none | Expand a portrait phone photo whose EXIF orientation is 6 |
| 21 | **Video probe**: does a submission whose body is not JSON (section 10) cost anything? The image arm's empty-body probe is NOT safe here - `fal-ai/kling-video/v3/turbo/pro/text-to-video` declares `required: []`, so `{}` is a valid 5 s request | Nothing: the queue cannot deserialise the body, so no runner can dispatch it and no output exists to bill | Run a connection test on a fal video row and check the fal usage page shows no charge |
| 22 | Whether **every** video endpoint accepts a base64 **data URI** in its image field. fal-cdn documents the form platform-wide ("Models also accept base64-encoded data URIs") and warns a URL "is the universal format that works with every model"; only `fal-ai/vidu/q4/image-to-video` restates it per field | The frame is sent as a data URI, so a large frame travels inline (fal: "not recommended for files larger than a few KB") | Send one real frame as a data URI to each endpoint and confirm a clip comes back |
| 23 | The **tracking URL shape** on a real key. Measured anonymously on 2026-10-16: the endpoint-shaped URL (what fal's own queue page shows) answers 405, the app-scoped one answers 404 `{"status":"NOT_FOUND"}` | The client uses the URLs the submit answer returned and never rebuilds one, but only when they share the scheme, host and port of the configured queue endpoint and pass the public-host guard (S-50/S-57: `FalQueueClient.RequireQueueOrigin`; a URL elsewhere fails the take without a request); the probe still POSTs to `{base}/{model}` | Submit one take, restart the engine, and confirm the persisted handle's URLs poll to COMPLETED |
| 24 | Whether a 4xx on the result route really means "refused, nothing billed" - the client FAILS the take there rather than keeping it charged | Refused and unbilled | Force one invalid take (a duration outside the schema, submitted by hand) and check the invoice |
| 25 | Whether `prompt_expansion_mode: "balanced"` on `minimax/h3-max-turbo/*` (schema-required, schema-defaulted) behaves like omitting it | Sent explicitly, because the schema marks it required | Compare one clip with it sent against one without |
| 26 | Audio: `generate_audio` defaults to true on `fal-ai/veo3.1*` and is never forwarded, so the cards are the WITH-audio figures | Not sent | Confirm one veo take is billed at the with-audio rate |
| 27 | Retention: our prompts and frames are stored for 30 days unless `X-Fal-Store-IO: 0` is sent | The header is NOT sent on an ordinary request - what the vendor keeps is the operator's decision, not a client default. An avatar request (a person's still, pictures or speech, section 13) always sends it | Confirm the prompt appears under Dashboard > Recent History, then decide the deployment's policy |
| 28 | **Audio probe** (section 11): what an unknown request id answers on `GET {base}/fal-ai/elevenlabs/requests/{uuid}/status` with a valid key | 404 = key accepted, nothing created (anonymously the app-scoped route answers 404, row 23); 401/403 = key rejected | Run a connection test on a fal music row with a real and with a wrong key |
| 29 | Which ElevenLabs model version `fal-ai/elevenlabs/music` calls, and what it does when both or neither of `prompt`/`composition_plan` are sent | The client sends exactly one of them (refuses both or neither before submit) | One call each way; compare with a direct ElevenLabs call |
| 30 | `fal-ai/elevenlabs/music` billed minutes for a 30 s track (expected 1) and the enforced maximum (schema 600000 ms, ElevenLabs docs 5 min) | Each started minute is a unit; the platform caps requests at 300 s | Invoice line for one 30 s and one 61 s track |
| 31 | `fal-ai/elevenlabs/sound-effects/v2`: requested or delivered seconds billed, and what an unset `duration_seconds` bills; true maximum (schema 22 s, direct API 30 s) | Each started second of the REQUESTED duration; an unset one is estimated at 22 s | Invoice line for one 1.5 s effect and one with no duration |
| 32 | `content_type` and file for `opus_*` outputs (pcm/ulaw/alaw are not offered: no container to probe) | Extension and type follow the `output_format` sent (`.mp3` `audio/mpeg`, `.opus` `audio/ogg`) | Fetch one Opus output and check the header and that ffprobe reads its duration |
| 33 | MMAudio output really is FLAC for every request (its example is `.flac` with `application/octet-stream`) | `.flac`, `audio/flac` | Fetch one and run ffprobe |
| 34 | **MUST CONFIRM (licence, product decision 7): ElevenLabs terms may still apply to their models used via fal.** The platform relies on fal's commercial terms for `fal-ai/elevenlabs/music` and `fal-ai/elevenlabs/sound-effects/v2`; elevenlabs.io/music-terms (individual-use tiers, model resale restricted) may still bind output generated through fal | fal's terms govern; the direct ElevenLabs route is dormant (no kind enabled, no rows, no keys) | Get written confirmation from fal (or ElevenLabs) that multi-tenant commercial use of these endpoints' output is covered; record the answer here. Internal only: not stated in customer docs |
| 35 | Whether fal also bills the **source picture's own megapixels** on reference calls (per-megapixel endpoints: fill, erase, outpaint, `iclight-v2`, `seedvr`, `qwen-image-edit-2511`) | Billed at the delivered output size; Qwen adds each reference's MP; `flux-2-pro/outpaint` bills `max(input MP, output MP)`, at least 2 MP when the output is 1 MP or less (its price names input); for every other endpoint, Qwen included, the source's own MP is not added. The editor's hold uses the expected size, else the documented maximum, else the largest row; the meter clamps a measured size (documented maximum, else 4 x expected, else `OutputSizePricing.FallbackMaxOutputPixels`) and a measured, priced job settles at what it metered, uncapped (floored at the hold only when metering failed after the vendor answered). On a linear per-megapixel endpoint (fill, `iclight-v2`, `seedvr`, `qwen-image-edit-2511`) an `{n}MP` size with no row of its own is n times the per-megapixel rate; erase and outpaint keep the next row up. A `422` remains only for an unreadable Qwen reference picture; see [metering-and-billing.md](metering-and-billing.md#the-credit-ledger-grants-holds-and-refunds) | Compare the invoice line with output MP, and with output plus source MP, on one reference call |
| 36 | **Topaz upscale billing** (`fal-ai/topaz/upscale/video`, section 13): does a 50 fps output bill as "60fps" (doubled)? Is audio kept or dropped? Max input length and size? Is the billed length the OUTPUT length? | Anything above 30 fps is priced doubled (the honest upper bound); the platform never relies on the vendor's audio (the source audio is remuxed locally); requests are capped at 120 s | Upscale one 10 s 50 fps clip, then one 10 s 30 fps clip; compare invoice lines with the output seconds and tier; ffprobe the result for an audio stream |
| 37 | **RIFE/FILM compute seconds per output frame** (`fal-ai/rife/video`, `fal-ai/film/video`): the result carries no compute time | `VideoEnhance:Hosted:ComputeSecondsPerOutputFrame` = 0.05 at 1920x1080, scaled by pixels, rounded up to whole compute seconds; the hold IS the settlement (UNMEASURED) | One 2x and one 4x run at 720p, 1080p and 4K; divide the invoice compute seconds by the output frames and replace the setting |
| 38 | **Output frame rate exactness**: does RIFE with `use_calculated_fps: true` return exactly source fps x (`num_frames` + 1)? Does bytedance return exactly the `target_fps` sent (we send the source rate rounded and clamped to 24..120)? | The local conform restores the exact rate, so a small vendor drift is tolerated; a large one is a bug to report | ffprobe `r_frame_rate` and `nb_frames` of each result against the source |
| 39 | **Scene detection**: does `use_scene_detection: true` remove smear frames at cuts on RIFE and FILM (we send it)? And quality of 4x on fast motion (ghosting) | Sent true; 4x offered | Interpolate a clip with a hard cut and a fast pan; inspect the frames around the cut |
| 40 | **Retention and training** of the customer's clip sent to fal as a presigned GET (`X-Fal-Store-IO` is not sent) | Unknown until measured; stated as unknown in `docs/provider-data-handling.md` | Same check as row 27 for a video input |
| 41 | **bytedance** `duration` ("as used for billing") against the clip length; whether `scale_ratio` really overrides `target_resolution`; the 2K price (we price 2K as 4K) | The platform has no 2K tier; `enhancement_tier: "standard"` is pinned | Upscale one clip to 1.5x and 2x; compare the invoice with the reported `duration` |
| 42 | **Reference-to-video and scene stills** (section 15, added 2026-11-16): data URIs and the body ceiling on `alibaba/wan-3.0/reference-to-video` and the two `fal-ai/veo3.1` reference routes, their real picture limits and durations, the 4k and Fast prices, `X-Fal-Store-IO: 0` on them, how well each model keeps two or three subjects apart, and the real cost of a six-picture scene still | The assumptions of section 15's table R1-R10 | One take per route with a person and an object, and one scene still; read the invoice lines |
| 43 | **ImageGenerate output size per resolution tier** of `google/nano-banana-2.1` and its `/edit` (added 2026-11-21): the real pixel size delivered at `resolution` 1K, 2K and 4K for each aspect ratio | The tiers deliver roughly a 1024 / 2048 / 4096 long edge. **No live call has been made**; record the real delivered sizes per tier here when one is. The step's final resize guarantees the exact `OutputWidth` x `OutputHeight` regardless, and warns `image_upscaled` when the picture arrives smaller than asked | One generate per tier and aspect (1:1, 16:9, 4:5); read the delivered `width`/`height` from the step output |

## Which capability each editor tool resolves

Each picture-editor tool resolves its own model capability (`ImageJobCapabilities`), so a fal.ai setup is
one provider holding one model per capability, each model code an endpoint of ITS job (the API refuses a
mismatch). The model code is what the tool calls; the defaults below are what **Set up all picture tools**
adds under the provider. A tier-priced endpoint is priced with one price row per tier, the tier as the
row's `variant` ([docs/metering-and-billing.md](metering-and-billing.md) "fal.ai images").

| Tool | Capability | Default endpoint | Other endpoints a row may name |
|---|---|---|---|
| erase | `ImageErase` | `fal-ai/object-removal/mask` | `fal-ai/flux-pro/v1/erase` |
| fill | `ImageFill` | `fal-ai/flux-pro/v1/fill` | `ideogram/v4.5/edit` |
| expand | `ImageExpand` | `fal-ai/bria/expand` | `fal-ai/flux-2-pro/outpaint` (margins only, no prompt, 4 MP) |
| replaceBackground | `ImageBackgroundReplace` | `fal-ai/bria/background/replace` | |
| relight | `ImageRelight` | `fal-ai/image-apps-v2/relighting` | `fal-ai/iclight-v2`, `bria/fibo-edit/relight` |
| upscale | `ImageUpscale` | `fal-ai/seedvr/upscale/image` | `topaz/upscale/image/precision`, `topaz/upscale/image/generative` |
| instruct | `ImageEdit` | `google/nano-banana-2.1/edit` | the other instruction-edit endpoints of the catalogue |
| generate | `ImageGeneration` | `ideogram/v4.5` | `google/nano-banana-2.1`, FLUX.3, Seedream 5 Pro, `fal-ai/ideogram/v3/generate-transparent` |
| subject, removeBackground | `Segmentation` | `fal-ai/sam-3-1/image` (removeBackground: `fal-ai/birefnet/v2`) | section 1 |

A tool with no row of its own falls back to the `ImageEdit` default row when that row's kind serves it; on a
fal.ai `ImageEdit` row the tool then runs on its catalogue default above, never on the row's instruction-edit
model. Billing is unchanged by the split: every job is metered `ImageEditImages` (generate `ImageGenImages`)
under the endpoint that ran, priced from `ModelPriceCatalog.FalImages`. `web/lib/inference/image-capabilities.ts`
mirrors the table for the admin form, checked against the catalogue by a .NET test.

## How facts were established

| Fact | Source read |
|---|---|
| Input/output schema | `https://fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}` (HTTP 200 = endpoint exists; HTTP 404 with body `null` = it does not) |
| Price | The sentence rendered on `https://fal.ai/models/{id}` ("Your request will cost ..."), plus the `endpointBilling` object embedded in the same page, plus (for ids in the gallery) the `pricingInfoOverride` field of `https://fal.ai/api/models?categories=image-to-image` / `...=text-to-image` |
| Discovery | `https://fal.ai/api/models?categories=image-to-image&page=N` (402 items over 11 pages) and `...=text-to-image` (203 items over 6 pages) - a JSON listing with `id`, `title`, `category`, `tags`, `licenseType`, `deprecated`, `pricingInfoOverride` |
| Terms and retention | `https://fal.ai/legal/terms-of-service` (Last Updated: September 8, 2026), `https://fal.ai/legal/privacy-policy`, `https://docs.fal.ai/faq`, `https://docs.fal.ai/docs/documentation/model-apis/media-expiration` |

Notes on price reading: (1) A page statement of "$0 per compute second" (six endpoints) is shown verbatim but is **not treated as a real price** - its embedded billing metadata also says 0; the true cost is unknown. (2) Where the page has no sentence, the embedded billing metadata and/or the gallery field is quoted instead. (3) Some endpoints are billed per megapixel; the pages do not always say whether input, output or both megapixels count. (4) A megapixel is metered as **1,048,576 pixels** (1024 x 1024), rounded up (product decision, `OutputSizePricing.PixelsPerMegapixel`). That matches fal's own outpaint example, where 1024x1024 is one megapixel and 1920x1080 (1.98 binary MP) is two.

## Already integrated in the repo (not re-documented)

`fal-ai/evf-sam`, `fal-ai/sam2/image`, `fal-ai/birefnet/v2`, `fal-ai/imageutils/depth`, `fal-ai/imageutils/marigold-depth` and (since the thumbnail-editor fal arm) `fal-ai/sam-3-1/image` ($0.01 per request; section 9 has its schema); `fal-ai/sam-3-1/image-rle` and `fal-ai/lama` were NOT integrated by that arm (the repo brief listed them). `fal-ai/lama` takes image_url + mask_image_url and returns image. Observation only: the `fal-ai/lama` model page currently renders "Your request will cost $0 per compute second." (billing metadata price 0).

## Common objects

Most endpoints return the standard fal file object. Its fields are identical wherever it is written "standard fal file object" below:

| property | type | required | description |
|---|---|---|---|
| `url` | string | yes | The URL where the file can be downloaded from. |
| `content_type` | string or null | no | The mime type of the file. |
| `file_name` | string or null | no | The name of the file. It will be auto-generated if not provided. |
| `file_size` | integer or null | no | The size of the file in bytes. |
| `width` | integer or null | no | The width of the image in pixels. |
| `height` | integer or null | no | The height of the image in pixels. |

Variants that differ (and are therefore printed in full where they occur): `File` objects without `width`/`height`; the older `registry__image__fast_sdxl__models__Image` object (`url`, `width`, `height` required; `content_type` default "image/jpeg"; no `file_size`/`file_name`). Where a property is typed `ImageSize (object) | string`, the object form is `{width, height}` (integers, exclusive minimum 0, maximum 14142, default 512 each) and the string form is one of the listed enum names.

Property `sync_mode` is, on nearly every endpoint, described as: "If `True`, the media will be returned as a data URI and the output data won't be available in the request history."

## Mask polarity and coordinate conventions - what the schemas actually say

This is the part most likely to cause a silent bug. Only the rows marked STATED can be implemented without measuring.

| Endpoint | Mask / coordinate statement in the schema | Status |
|---|---|---|
| `fal-ai/flux-pro/v1/erase` | "white (255) marks pixels to erase and black (0) marks pixels to keep. Needs to match the dimensions of the input image." | STATED |
| `fal-ai/object-removal/mask` | "White pixels (255) indicate areas to remove." | STATED (size match not stated) |
| `fal-ai/finegrain-eraser/mask` | "binary mask where white (255) indicates areas to erase" | STATED |
| `fal-ai/ideogram/object-removal` | "A black-and-white mask matching the source image dimensions. White pixels are removed and black pixels are preserved" | STATED |
| `fal-ai/inpaint` | "Black areas will be preserved, white areas will be inpainted." | STATED |
| `ideogram/v4.5/edit` | "Black edits, white preserves. Must contain both regions after rounding." | STATED - and **opposite** to the four above |
| `fal-ai/bria/eraser` | "binary mask image that represents the area that will be cleaned" | NOT STATED |
| `fal-ai/bria/genfill` | same wording as bria/eraser | NOT STATED |
| `bria/genfill/v2` | "The mask image." | NOT STATED |
| `fal-ai/flux-pro/v1/fill`, `.../fill-finetuned` | "Needs to match the dimensions of the input image." | NOT STATED |
| `fal-ai/ideogram/v3/edit` | "MUST have the exact same dimensions (width and height) as the input image." | polarity NOT STATED; size match STATED |
| `fal-ai/qwen-image-edit/inpaint` | "The URL of the mask for inpainting" | NOT STATED |
| `fal-ai/gpt-image-1.5/edit` (`mask_image_url`), `openai/gpt-image-2/edit` (`mask_url`) | "indicates what part of the image to edit" | NOT STATED |
| `fal-ai/iclight-v2` (`mask_image_url`) | "URL of mask to be used for ic-light conditioning image" | NOT STATED |
| `fal-ai/sam-3/image` input points/boxes | integers named `x`,`y` / `x_min`,`y_min`,`x_max`,`y_max`; label "1 for foreground, 0 for background" | polarity of labels STATED; coordinate space (pixels vs normalized) NOT STATED |
| `fal-ai/sam-3/image` output boxes | "normalized bounding boxes [cx, cy, w, h]" / "normalized cxcywh coordinates" | STATED (differs from the input convention) |
| `fal-ai/bria/expand` | `canvas_size`, `original_image_size`, `original_image_location`: integer arrays | element order not stated in the field descriptions; RESOLVED from fal's own schema example (`canvas_size` `[1200, 674]` answered by a 1200x674 image) and Bria's API reference: `[width, height]` and `[x, y]` - see its section |



## 1. Subject segmentation and background removal


### `fal-ai/sam-3/image`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Segment Anything Model 3", category `image-to-image`, tags ["segmentation", "mask", "real-time"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.005 per request." - <https://fal.ai/models/fal-ai/sam-3/image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.005, `is_partner_api` = false - <https://fal.ai/models/fal-ai/sam-3/image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/sam-3/image>  (input schema `Sam3ImageInput`, output schema `Sam3ImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Sam3ImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to be segmented |
| `prompt` | string | no | "wheel" | - | Text prompt for segmentation |
| `text_prompt` | string \| null | no | - | - | [DEPRECATED] Use 'prompt' instead. Kept for backward compatibility. |
| `point_prompts` | array<PointPrompt (object)> | no | [] | - | List of point prompts |
| `box_prompts` | array<BoxPrompt (object)> | no | [] | - | Box prompt coordinates (x_min, y_min, x_max, y_max). Multiple boxes supported - use object_id to group boxes for the same object or leave empty for separate objects. |
| `apply_mask` | boolean | no | true | - | Apply the mask on the image. |
| `sync_mode` | boolean | no | false | - | If True, the media will be returned as a data URI. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `return_multiple_masks` | boolean | no | false | - | If True, upload and return multiple generated masks as defined by `max_masks`. |
| `max_masks` | integer [minimum=1, maximum=32] | no | 3 | - | Maximum number of masks to return when `return_multiple_masks` is enabled. |
| `include_scores` | boolean | no | false | - | Whether to include mask confidence scores. |
| `include_boxes` | boolean | no | false | - | Whether to include bounding boxes for each mask (when available). |

*Nested `PointPrompt` (at `point_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `point_prompts.x` | integer \| null | no | - | - | X Coordinate of the prompt |
| `point_prompts.y` | integer \| null | no | - | - | Y Coordinate of the prompt |
| `point_prompts.label` | integer \| null | no | - | [0, 1] | 1 for foreground, 0 for background |
| `point_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Prompts sharing an object id refine the same object. When a text prompt is also given, the id selects which detected object the points refine. |
| `point_prompts.frame_index` | integer \| null | no | - | - | The frame index to interact with. |

*Nested `BoxPrompt` (at `box_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `box_prompts.x_min` | integer \| null | no | - | - | X Min Coordinate of the box |
| `box_prompts.y_min` | integer \| null | no | - | - | Y Min Coordinate of the box |
| `box_prompts.x_max` | integer \| null | no | - | - | X Max Coordinate of the box |
| `box_prompts.y_max` | integer \| null | no | - | - | Y Max Coordinate of the box |
| `box_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Boxes sharing an object id refine the same object. |
| `box_prompts.frame_index` | integer \| null | no | - | - | The frame index to interact with. |

**OUTPUT** (`Sam3ImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) \| null | no | - | - | Primary segmented mask preview. |
| `masks` | array<Image (object)> | yes | - | - | Segmented mask images. |
| `metadata` | array<MaskMetadata (object)> \| null | no | - | - | Per-mask metadata including scores and boxes. |
| `scores` | array<number \| null> \| null | no | - | - | Per-mask confidence scores when requested. |
| `boxes` | array<array<number> \| null> \| null | no | - | - | Per-mask normalized bounding boxes [cx, cy, w, h] when requested. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

*Nested `MaskMetadata` (at `metadata`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `metadata.index` | integer | yes | - | - | Index of the mask inside the model output. |
| `metadata.score` | number \| null | no | - | - | Score for this mask. |
| `metadata.box` | array<number> \| null | no | - | - | Bounding box for the mask in normalized cxcywh coordinates. |

**Use in the editor:** Click/box/text-prompted subject mask; same family as the already-integrated sam-3-1/image. Its page states $0.005 per request versus the $0.01 recorded for sam-3-1 in the repo brief.

**Gaps:** Input point/box coordinates are plain integers and the schema does not say whether they are pixels of the input image or normalized values; output `boxes`/`metadata.box` are stated to be normalized [cx, cy, w, h] (input uses x_min/y_min/x_max/y_max) - the two conventions differ. Mask polarity of `masks[]` and whether `apply_mask=true` returns an RGBA cut-out or a visualization is not stated (`image` is described only as 'Primary segmented mask preview'). Default `prompt` is 'wheel'.


### `fal-ai/sam-3-1/video`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: not present in the image-to-image / text-to-image listings.
- Price:
  - Page statement (verbatim): "Your request will cost $0.01 per 16 frames of video input." - <https://fal.ai/models/fal-ai/sam-3-1/video>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = false - <https://fal.ai/models/fal-ai/sam-3-1/video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/sam-3-1/video>  (input schema `Sam31VideoInput`, output schema `Sam31VideoOutput`)
- Category (x-fal-metadata): `video-to-video`

**INPUT** (`Sam31VideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video_url` | string | yes | - | - | The URL of the video to be segmented. |
| `prompt` | string | no | "" | - | Text prompt for segmentation. Use commas to track multiple objects (e.g., 'person, cloth'). |
| `point_prompts` | array<PointPromptBase (object)> | no | [] | - | List of point prompts |
| `box_prompts` | array<BoxPromptBase (object)> | no | [] | - | List of box prompt coordinates (x_min, y_min, x_max, y_max). |
| `apply_mask` | boolean | no | true | - | Apply the mask on the video. |
| `video_output_type` | string | no | "X264 (.mp4)" | ["X264 (.mp4)", "VP9 (.webm)"] | The output type of the generated video. |
| `detection_threshold` | number [minimum=0.1, maximum=1] | no | 0.5 | - | Detection confidence threshold (0.0-1.0). Lower = more detections but less precise. |
| `max_num_objects` | integer [minimum=1, maximum=128] | no | 16 | - | Maximum number of objects to track in the video. |

*Nested `PointPromptBase` (at `point_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `point_prompts.x` | integer \| null | no | - | - | X Coordinate of the prompt |
| `point_prompts.y` | integer \| null | no | - | - | Y Coordinate of the prompt |
| `point_prompts.label` | integer \| null | no | - | [0, 1] | 1 for foreground, 0 for background |
| `point_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Prompts sharing an object id refine the same object. When a text prompt is also given, the id selects which detected object the points refine. |

*Nested `BoxPromptBase` (at `box_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `box_prompts.x_min` | integer \| null | no | - | - | X Min Coordinate of the box |
| `box_prompts.y_min` | integer \| null | no | - | - | Y Min Coordinate of the box |
| `box_prompts.x_max` | integer \| null | no | - | - | X Max Coordinate of the box |
| `box_prompts.y_max` | integer \| null | no | - | - | Y Max Coordinate of the box |
| `box_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Boxes sharing an object id refine the same object. |

**OUTPUT** (`Sam31VideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | File (object) | yes | - | - | The segmented video. |
| `boundingbox_frames_zip` | File (object) \| null | no | - | - | Zip file containing per-frame bounding box overlays. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.content_type` | string \| null | no | - | - | The mime type of the file. |
| `video.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Video object tracking/segmentation. Not an image-editor job; relevant only if thumbnails are ever cut from video.

**Gaps:** Billing is per 16 frames of video input, so cost scales with clip length. Output is a video, not masks per frame.


### `fal-ai/sam-3-1/video-rle`

- **Status: established**; schema HTTP 200. The endpoint prompted region effects ship against
  (`FalAiVideoSegmentationClient`, `InferenceProviderCapability.VideoSegmentation`), one call per
  source clip and prompt. The client, the RLE decoder and the mask store are merged and tested against
  committed real responses; no shipping code has made a live call.
- Gallery entry: the video catalogue lists it with `licenseType` "commercial" (read 2026-10-18).
- Price:
  - Page statement (verbatim): "Your request will cost $0.01 per 16 frames of video input." -
    <https://fal.ai/models/fal-ai/sam-3-1/video-rle> - read **2026-10-18** (the sibling initiative's
    trial pass; not re-read for this row).
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01.
  - Corroborated by three real calls the same day: 120 frames billed as 8 units = $0.08, 158 frames
    as 10 units = $0.11 (the rate-card estimate; the connector exposes no billed figure).
  - **Where the price lives in code:** `ModelPriceCatalog.FalVideoSegmentation*` holds the block price
    and its size, exposes the per-frame price a rate card multiplies ($0.000625) and carries this
    sentence and read date as its basis; `RateCardSeed`'s `VideoSegmentationFrames` wildcard is
    derived from that same row, never restated. **No `ModelCatalogSeed` row is added this wave**
    (D-14): that seed is generated from the image and video-generation catalogues, and listing a
    video-segmentation id needs a generator and research-doc change outside this wave — so the
    generated catalog does not offer the model, and the price card plus the rate-card seed are the
    authority. The gap is reported, not hidden.
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/sam-3-1/video-rle>

**INPUT** (as read from the schema on 2026-10-18)

| property | type | required | default | enum / range |
|---|---|---|---|---|
| `video_url` | string | yes | - | - |
| `prompt` | string | no | `""` | - |
| `point_prompts` | array of object | no | `[]` | see below |
| `box_prompts` | array of object | no | `[]` | see below |
| `mask_url` | string \| null | no | null | an initial mask to apply |
| `frame_index` | integer | no | 0 | the frame the initial mask is applied from |
| `max_num_objects` | integer | no | 16 | 1 to 128 |
| `detection_threshold` | number | no | 0.5 | 0.01 to 1 (the page adds: "Try 0.2-0.3 if text prompts fail") |
| `apply_mask` | boolean | no | **false** | - |
| `boundingbox_zip` | boolean | no | false | - |
| `return_object_rles` | boolean | no | **false** | "Also return every tracked object's own mask in each frame, with an id the object keeps for the whole video. The merged rle is returned as before." (verbatim) |

- `point_prompts[]`: `{object_id, x, y, label, frame_index}` — `label` 0 = background, 1 = foreground.
- `box_prompts[]`: `{object_id, x_min, y_min, x_max, y_max, frame_index}`. There is no label on a box. The client used to send `x`, `y`, `width`, `height` and a `label`; it now sends `x_min`/`y_min`/`x_max`/`y_max` (the corner pair, `x_max = x_min + width`), as this schema says. No live call has confirmed it yet.

Whether `x`/`x_min`/`x_max` are source **pixels** or a normalised grid is **not stated** for this endpoint
either (the same open question as the image variant, "Must measure with a real key" row 1). The
shipping client sends integers; a customer's hand-drawn box arrives from the artifact's normalised
quad, so the conversion lives in the analyze path, not in this sheet.

**OUTPUT — the schema, and the real answer**

The schema declares two keys, and **neither of them is the RLE**:

| property | type | required | description (verbatim) |
|---|---|---|---|
| `video` | File | yes | "The segmented video." |
| `boundingbox_frames_zip` | File \| null | no | "Zip file containing per-frame bounding box overlays." |

A real response (committed at
`inference/tests/ReelBolt.WorkflowEngine.Tests/TestData/prompted-region-effects/sam31-rle/`, request
`01a14d4e-a603-7e10-97aa-2a4bdb8e6973`) carries six keys:

| key | shape | what it is |
|---|---|---|
| `rle` | array of string, one per frame | the **union** of every tracked object's mask on that frame; `""` when nothing was found |
| `objects` | array (per frame) of `{object_id, rle}` | **per-object masks, one entry per tracked object**; empty array on a frame with nothing |
| `boxes` | array (per frame) of `[number, number, number, number]` \| null | the vendor's per-frame box, **normalised `[cx, cy, w, h]`** |
| `metadata` | array of `{index, score, box}` | the same box again, with `score` (null in all three committed runs) |
| `scores` | null | not reported by this endpoint in these runs; null is "not reported", never zero |
| `boundingbox_frames_zip` | null | null because the trial sent `boundingbox_zip: false` |

Layout facts, measured on the committed responses rather than assumed:

- **On a single-object frame** the top-level `rle[i]` equals that object's own mask, byte for
  byte — measured on all 120 frames of R1/tabby.
- **On a multi-object frame the top-level `rle` and `boxes[i]` are the UNION** over every object on
  that frame, not any one object's mask: on the 59 two-object frames of R1/turn-a the top-level string
  matches **neither** object's mask. Only `objects[].rle` is per object, and it is the only thing
  ReelBolt reads (D-13).
- A run list is `"start length start length …"`, **0-based**, over a **row-major** index of a frame
  `W` wide; `W × H` is the **source clip's own size** (768x1344 for the committed clips).
- A run means "255 here", everything else is 0. Runs never overlap and never leave the frame, and a
  run **may legally cross a row boundary** (a sliver at the right edge continues at column 0 of the
  next row). That is not an edge case: 20,517 of the 73,985 runs on R1/tabby cross a row boundary, on
  76 of its 120 frames, because the mask touches the right edge. A "runs never wrap" rule would reject
  most of a real clip; the bound that matters is the linear index against `W × H` (D-11).
- `boxes`/`metadata[].box` are **centre-anchored** `[cx, cy, w, h]`, normalised: reading them as
  corners puts the box about a half-height above the mask (median 21% of the frame diagonal, worst
  52%), and on a multi-object frame they are the union box. They are null where the mask is empty.
  They are **never used for correctness**: `VideoSegmentationTrack.BoxKeyframes` is derived from the
  decoded mask (D-05), and the one place a vendor box is read is a decoder cross-check — at least 95%
  of a decoded mask must land inside its own box grown by 2 px, which is what catches a wrong grid or
  a 1-based reading, and a failure is a refusal, never a shifted mask.

**Retention.** The shipping client sends `X-Fal-Store-IO: 0` on every segmentation call, so the
vendor is asked not to store the clip or the masks. This differs deliberately from the image arm,
which leaves the header off ("Must measure with a real key" row 27): a customer's footage leaving the
host is an opt-in that comes with a retention request attached.

**The answer states no frame size, frame count, cost or scores — so the caller supplies the geometry.**
Nothing in the response says what the `W` of the row-major index is, how many frames the clip has, or
what the call cost; `scores` is null in every committed run. ReelBolt therefore passes a
`VideoSegmentationFrameSize(width, height, frameCount)` measured by its own ffprobe of the same clip,
and the client **refuses before submitting** when no such measurement is available:

> No IVideoSegmentationFrameSizeProbe is configured for 'fal-ai/sam-3-1/video-rle'. The vendor's
> response states no frame width or height, so the RLE cannot be decoded without the caller's own
> probe of the clip. Refusing to guess a width.

Guessing a width does not fail loudly; it produces a plausible mask sheared by a sub-pixel per frame,
which is why this is a refusal and not a default (D-12). A probe that answers a non-positive size is
`SEGMENTATION_BAD_RESPONSE` for the same reason.

**ReelBolt client, decoder and mask store** (all merged; no live call from shipping code):

| piece | where | what it does |
|---|---|---|
| `FalAiVideoSegmentationClient` | `ReelBolt.Shared/Inference` | submit/poll/fetch through `FalQueueClient`; sends `apply_mask:false`, `return_object_rles:true`, `boundingbox_zip:false`, the prompt and at most one point and one box from the track target's region, and `X-Fal-Store-IO: 0` |
| `FalAiVideoSegmentationClientFactory` | engine (`Services/Video/Tracking`) | the DI-registered factory: builds the client per (resolved provider, frame geometry), wraps it in `MeteringVideoSegmentationClient`, and hands it a probe answering the analyze step's own measurement |
| `Sam31VideoRleResponseReader` + `Sam31VideoRleDecoder` | `ReelBolt.Shared/Inference` | **one** decoder for the engine and for every test: reads `objects[].rle`, bounds-checks each run against `W × H` (an end overrun of at most 32 px is clamped and reported, a start outside or a larger overrun is `SEGMENTATION_BAD_RESPONSE`), and refuses an object mask that is an empty string |
| `SegmentationMaskStore` | engine (`Services/Segmentation`) | the only writer of `projects/{p}/agentFiles/segmentation/`: FFV1 `gray` at the source's own width, height, frame rate and frame count, named by the content key's SHA-256, so a repeated run downloads instead of buying again |

The source clip reaches the vendor as a **10-minute** presigned GET (`SegmentationTrackingService.SourceUrlTtl`),
scoped to the project prefix, minted last and only for work that will happen.

**Limits:** none stated by the vendor — no maximum duration, resolution or file size. ReelBolt
imposes its own: `MaxSegmentationSeconds` (60 s default, raisable to 600) refuses a longer source
before anything is sent, and the provider timeout is 60 to 3600 seconds.

**Measured download limit (prod, 2026-11-02).** The vendor DOES refuse a large file even though no
limit is documented: a 563 MB original (a valid presigned GET that answered 206 to an anonymous
ranged GET) came back after 8 s as `422`, `body.video_url`, `file_download_error`, "Failed to
download the file". Every input it had accepted was a few MB. So a source over
`SegmentationLimits.VendorSourceMaxBytes` (100 MB), or a rotated one, is never sent as the original: the engine sends a
**vendor copy** instead (`SegmentationMaskArgvBuilder.BuildVendorCopyArgs`): the same frames at the
same width and height (masks still index the source's own `W x H`), video only, upright, H.264 CRF 20
under a VBV ceiling of `100 MB x 8 x 0.85 / duration` (about 11 Mbit/s for 60 s, 1.1 Mbit/s for 600 s).
Its size is checked after the encode; a copy still over 100 MB is never stored or sent
(`SEGMENTATION_EGRESS_UNAVAILABLE`, refunded). Copies are cached by source hash under
`agentFiles/segmentation/vendor1/`. The exact vendor ceiling is still unknown (must-measure: the
largest file `sam-3-1/video-rle` downloads); a refused result fetch now names the field, type and the
vendor's sentence (never the URL it echoes) in the step error.

**Licence:** catalogue `licenseType` "commercial".

**Gaps:** the RLE keys are undocumented; the vendor echoes no frame size, frame count or duration;
whether `video` is a mask, an overlay or a cut-out is not stated and ReelBolt never downloads it;
point/box coordinate space is unstated. The decode is therefore pinned by committed real responses,
not by the sheet.

### `fal-ai/sam-3-1/image/batch`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404 (body `null`); model page `https://fal.ai/models/fal-ai/sam-3-1/image/batch` returns HTTP 200 but is the site's not-found shell (no title, no billing metadata); not in the public gallery listing.

### `fal-ai/sam2/auto-segment`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Segment Anything Model 2", category `image-to-image`, tags ["segmentation", "mask"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0 per compute second." - <https://fal.ai/models/fal-ai/sam2/auto-segment>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/sam2/auto-segment>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/sam2/auto-segment>  (input schema `Sam2AutoSegmentInput`, output schema `Sam2AutoSegmentOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Sam2AutoSegmentInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to be automatically segmented |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `output_format` | string | no | "png" | ["jpeg", "png"] | The format of the generated image. |
| `points_per_side` | integer | no | 32 | - | Number of points to sample along each side of the image. |
| `pred_iou_thresh` | number | no | 0.88 | - | Threshold for predicted IOU score. |
| `stability_score_thresh` | number | no | 0.95 | - | Threshold for stability score. |
| `min_mask_region_area` | integer | no | 100 | - | Minimum area of a mask region. |

**OUTPUT** (`Sam2AutoSegmentOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `combined_mask` | Image (object) | yes | - | - | Combined segmentation mask. |
| `individual_masks` | array<Image (object)> | yes | - | - | Individual segmentation masks. |

*Nested `Image` (at `combined_mask`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `combined_mask.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `combined_mask.content_type` | string \| null | no | - | - | The mime type of the file. |
| `combined_mask.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `combined_mask.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `combined_mask.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `combined_mask.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Whole-image automatic segmentation (no prompt) - could drive a 'click any region' picker without a per-click call.

**Gaps:** Price page shows '$0 per compute second' (billing metadata price 0) - treat the real cost as UNKNOWN. Mask format (binary vs colour-coded) of `combined_mask` / `individual_masks` and their polarity are not stated. `points_per_side` and thresholds carry no min/max in the schema.


### `fal-ai/imageutils/rembg`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Remove Background", category `image-to-image`, tags ["background removal", "utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0 per compute second." - <https://fal.ai/models/fal-ai/imageutils/rembg>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/imageutils/rembg>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/imageutils/rembg>  (input schema `ImageutilsRembgInput`, output schema `ImageutilsRembgOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageutilsRembgInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input image url. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `crop_to_bbox` | boolean | no | false | - | If set to true, the resulting image be cropped to a bounding box around the subject |

**OUTPUT** (`ImageutilsRembgOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Background removed image. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Simple background removal returning one image. Birefnet/v2 is already integrated, so this adds nothing unless it is cheaper.

**Gaps:** Price page shows '$0 per compute second' - real cost UNKNOWN. Schema does not state that the output has an alpha channel (description: 'Background removed image').


### `fal-ai/bria/background/remove`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria RMBG 2.0", category `image-to-image`, tags ["background removal", "image segmentation", "high resolution", "utility", "rembg"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.018 per generation." - <https://fal.ai/models/fal-ai/bria/background/remove>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.018, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/background/remove>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/background/remove>  (input schema `BriaBackgroundRemoveInput`, output schema `BriaBackgroundRemoveOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaBackgroundRemoveInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input Image to erase from |
| `prompt` | string \| null | no | - | - | Optional. Change what the cutout keeps in plain words: add ('the person with the mat'), drop ('without the lamp') or narrow ('only the chair'). Billed at $0.03 instead of $0.018. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`BriaBackgroundRemoveOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Background removal (RMBG 2.0 per the page text). Optional `prompt` changes what the cut-out keeps - a text-steerable cut-out.

**Gaps:** Schema states a prompt raises the price from $0.018 to $0.03 (in the `prompt` description). Alpha channel in the output is not stated in the schema. `image_url` description is copy-pasted ('Input Image to erase from').


### `fal-ai/ideogram/remove-background`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram Remove Background", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.01." - <https://fal.ai/models/fal-ai/ideogram/remove-background>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/remove-background>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/remove-background>  (input schema `IdeogramRemoveBackgroundInput`, output schema `IdeogramRemoveBackgroundOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramRemoveBackgroundInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The image URL whose background needs to be removed. The foreground subject is preserved against a transparent background. JPEG, PNG and WebP formats are supported (maximum file size 10MB). |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`IdeogramRemoveBackgroundOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The foreground image with the background removed (transparent). |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Cheap background removal; schema explicitly says the foreground is preserved against a transparent background.

**Gaps:** Max input file size 10MB per the `image_url` description. Edge quality vs birefnet is not measurable from the schema.


### `pixelcut/background-removal`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Pixelcut Background Remover", category `image-to-image`, tags ["background removal", "utility", "remove background"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.016 per image." - <https://fal.ai/models/pixelcut/background-removal>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.016, `is_partner_api` = true - <https://fal.ai/models/pixelcut/background-removal>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=pixelcut/background-removal>  (input schema `BackgroundRemovalInput`, output schema `BackgroundRemovalOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BackgroundRemovalInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input image (JPEG or PNG) |
| `output_format` | string | no | "rgba" | ["rgba", "alpha", "zip"] | Output format |
| `sync_mode` | boolean | no | true | - | When true, return result as a data URL instead of uploading to storage |

**OUTPUT** (`BackgroundRemovalOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) \| null | no | - | - | Result image (for rgba/alpha output formats) |
| `file` | File (object) \| null | no | - | - | Result file (for zip output format) |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

*Nested `File` (at `file`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `file.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `file.content_type` | string \| null | no | - | - | The mime type of the file. |
| `file.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `file.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Background removal with a selectable output: `rgba`, `alpha` (a matte) or `zip`. The `alpha` output would give a ready-made mask layer.

**Gaps:** `sync_mode` defaults to TRUE here (result returned as a data URL, not uploaded) - differs from every other endpoint; for large images the response will carry base64. Input restricted to JPEG or PNG. Contents of the `zip` output are not described.


## 2. Object removal, hole fill, inpainting


### `fal-ai/bria/eraser`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria Eraser", category `image-to-image`, tags ["image editing", "object removal"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generation." - <https://fal.ai/models/fal-ai/bria/eraser>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/eraser>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/eraser>  (input schema `BriaEraserInput`, output schema `BriaEraserOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaEraserInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input Image to erase from |
| `mask_url` | string | yes | - | - | The URL of the binary mask image that represents the area that will be cleaned. |
| `mask_type` | string | no | "manual" | ["manual", "automatic"] | You can use this parameter to specify the type of the input mask from the list. 'manual' opttion should be used in cases in which the mask had been generated by a user (e.g. with a brush tool), and 'automatic' mask type should be used when mask had been generated by an algorithm like 'SAM'. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `preserve_alpha` | boolean | no | false | - | If set to true, attempts to preserve the alpha channel of the input image. |

**OUTPUT** (`BriaEraserOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Remove an object given a user-painted or SAM-produced mask (`mask_type` manual/automatic).

**Gaps:** Mask polarity (which colour marks the area to clean) is NOT STATED - the description only says 'binary mask image that represents the area that will be cleaned'; must be measured. Whether the mask must match the image size is not stated. `mask_type` descriptions contain a typo ('opttion') but the enum is exact.


### `fal-ai/flux-pro/v1/fill`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX.1 [pro] Fill", category `image-to-image`, tags ["editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.05 per megapixel. Images are billed by rounding up to the nearest megapixel." - <https://fal.ai/models/fal-ai/flux-pro/v1/fill>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.05, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/v1/fill>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/v1/fill>  (input schema `FluxProV1FillInput`, output schema `FluxProV1FillOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FluxProV1FillInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to fill the masked part of the image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enhance_prompt` | boolean | no | false | - | Whether to enhance the prompt for better results. |
| `image_url` | string | yes | - | - | The image URL to generate an image from. Needs to match the dimensions of the mask. |
| `mask_url` | string | yes | - | - | The mask URL to inpaint the image. Needs to match the dimensions of the input image. |

**OUTPUT** (`FluxProV1FillOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<registry__image__fast_sdxl__models__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `registry__image__fast_sdxl__models__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Prompted fill of a masked area (inpaint) and, with an extended canvas + mask, outpaint. The 'hole fill after moving a subject' tool.

**Gaps:** Mask polarity NOT STATED (only 'Needs to match the dimensions of the input image'); must be measured. Output `images[]` here has only url/width/height/content_type (no file_size). Price is per megapixel, 'rounded up to the nearest megapixel'. `safety_tolerance` description says '1 being the most strict and 5 being the most permissive' but the enum allows up to '6'.


### `fal-ai/flux-pro/v1/fill-finetuned`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX.1 [pro] Fill Fine-tuned", category `image-to-image`, tags ["editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.06 per megapixel. Images are billed by rounding up to the nearest megapixel." - <https://fal.ai/models/fal-ai/flux-pro/v1/fill-finetuned>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.06, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/v1/fill-finetuned>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/v1/fill-finetuned>  (input schema `FluxProV1FillFinetunedInput`, output schema `FluxProV1FillFinetunedOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FluxProV1FillFinetunedInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to fill the masked part of the image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enhance_prompt` | boolean | no | false | - | Whether to enhance the prompt for better results. |
| `image_url` | string | yes | - | - | The image URL to generate an image from. Needs to match the dimensions of the mask. |
| `mask_url` | string | yes | - | - | The mask URL to inpaint the image. Needs to match the dimensions of the input image. |
| `finetune_id` | string | yes | - | - | References your specific model |
| `finetune_strength` | number [minimum=0, maximum=2] | yes | - | - | Controls finetune influence. Increase this value if your target concept isn't showing up strongly enough. The optimal setting depends on your finetune and prompt |

**OUTPUT** (`FluxProV1FillFinetunedOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<registry__image__fast_sdxl__models__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `registry__image__fast_sdxl__models__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Same as flux-pro/v1/fill but with a user fine-tune (`finetune_id`, `finetune_strength`). Only useful if brand/character fine-tunes are ever trained.

**Gaps:** Requires a fine-tune id that ReelBolt does not have today. Mask polarity NOT STATED.


### `fal-ai/inpaint`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Inpainting sdxl and sd", category `image-to-image`, tags ["editing", "diffusion"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0 per compute second." - <https://fal.ai/models/fal-ai/inpaint>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/inpaint>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/inpaint>  (input schema `InpaintInput`, output schema `InpaintOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`InpaintInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `model_name` | string | yes | - | - | URL or HuggingFace ID of the base model to generate the image. |
| `prompt` | string | yes | - | - | The prompt to use for generating the image. Be as descriptive as possible for best results. |
| `negative_prompt` | string | no | "" | - | The negative prompt to use. Use it to address details that you don't want in the image. This could be colors, objects, scenery and even the small details (e.g. moustache, blurry, low resolution). |
| `image_url` | string | yes | - | - | Input image for img2img or inpaint mode |
| `mask_url` | string | yes | - | - | Input mask for inpaint mode. Black areas will be preserved, white areas will be inpainted. |
| `num_inference_steps` | integer [minimum=0, maximum=150] | no | 30 | - | Increasing the amount of steps tells Stable Diffusion that it should take more steps to generate your final result which can increase the amount of detail in your image. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 7.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of Stable Diffusion will output the same image every time. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`InpaintOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image files info. |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Generic SD-family inpaint from any base model (`model_name` URL or HuggingFace id) - flexible but the quality depends on the chosen checkpoint.

**Gaps:** Price page shows '$0 per compute second' - real cost UNKNOWN. Mask polarity IS stated: 'Black areas will be preserved, white areas will be inpainted.' `model_name` has no enum - which ids are valid is not listed.


### `fal-ai/bria/genfill`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria GenFill", category `image-to-image`, tags ["image editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generation." - <https://fal.ai/models/fal-ai/bria/genfill>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/genfill>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/genfill>  (input schema `BriaGenfillInput`, output schema `BriaGenfillOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaGenfillInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input Image to erase from |
| `mask_url` | string | yes | - | - | The URL of the binary mask image that represents the area that will be cleaned. |
| `prompt` | string | yes | - | - | The prompt you would like to use to generate images. |
| `negative_prompt` | string | no | "" | - | The negative prompt you would like to use to generate images. |
| `seed` | integer [minimum=0, maximum=2147483647] \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of Images to generate. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`BriaGenfillOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated Images |
| `seed` | integer | yes | - | - | Seed value used for generation. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Prompted generative fill of a masked region (Bria).

**Gaps:** Mask polarity NOT STATED (same description as bria/eraser: 'area that will be cleaned'). `image_url` description is copy-pasted ('Input Image to erase from'). A newer `bria/genfill/v2` exists (below).


### `fal-ai/object-removal`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Object Removal", category `image-to-image`, tags ["utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.006 with Low Quality, $0.012 with Medium Quality, $0.018 with High Quality, and $0.024 with Best Quality" - <https://fal.ai/models/fal-ai/object-removal>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.006, `is_partner_api` = false - <https://fal.ai/models/fal-ai/object-removal>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/object-removal>  (input schema `ObjectRemovalInput`, output schema `ObjectRemovalOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ObjectRemovalInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to remove objects from. |
| `prompt` | string | yes | - | - | Text description of the object to remove. |
| `model` | string | no | "best_quality" | ["low_quality", "medium_quality", "high_quality", "best_quality"] | - |
| `mask_expansion` | integer [minimum=0, maximum=50] | no | 15 | - | Amount of pixels to expand the mask by. Range: 0-50 |

**OUTPUT** (`ObjectRemovalOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated images with objects removed. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Remove objects described by TEXT only (no mask supplied) - quickest 'remove the logo/person' tool; the `/mask` and `/bbox` siblings accept a mask or box.

**Gaps:** The service decides what to remove from the `prompt`; there is no way to inspect the mask it chose. `model` enum has no description (quality tiers are named in the price sentence). `mask_expansion` 0-50 px.


### `fal-ai/object-removal/mask`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Object Removal", category `image-to-image`, tags ["utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.006 with Low Quality, $0.012 with Medium Quality, $0.018 with High Quality, and $0.024 with Best Quality" - <https://fal.ai/models/fal-ai/object-removal/mask>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.006, `is_partner_api` = false - <https://fal.ai/models/fal-ai/object-removal/mask>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/object-removal/mask>  (input schema `ObjectRemovalMaskInput`, output schema `ObjectRemovalMaskOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ObjectRemovalMaskInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to remove objects from. |
| `mask_url` | string | yes | - | - | The URL of the mask image. White pixels (255) indicate areas to remove. |
| `model` | string | no | "best_quality" | ["low_quality", "medium_quality", "high_quality", "best_quality"] | - |
| `mask_expansion` | integer [minimum=0, maximum=50] | no | 15 | - | Amount of pixels to expand the mask by. Range: 0-50 |

**OUTPUT** (`ObjectRemovalMaskOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated images with objects removed. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Remove objects with a user mask - the best-specified eraser found: polarity, size matching and quality tiers are stated.

**Gaps:** Mask-to-image size matching is not stated (only 'White pixels (255) indicate areas to remove.'). `model` enum has no description.


### `fal-ai/flux-pro/v1/erase`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Flux Pro Erase", category `image-to-image`, tags ["utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 for the first megapixel generated, then $0.004 for each subsequent megapixel. Each reference megapixel also costs $0.004 set at 3 MP minimum." - <https://fal.ai/models/fal-ai/flux-pro/v1/erase>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.004, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/v1/erase>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/v1/erase>  (input schema `FluxProV1EraseInput`, output schema `FluxProV1EraseOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FluxProV1EraseInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the input image to erase content from. |
| `mask_url` | string | yes | - | - | The URL of a black/white mask image where white (255) marks pixels to erase and black (0) marks pixels to keep. Needs to match the dimensions of the input image. |
| `dilate_pixels` | integer [minimum=0, maximum=100] | no | 10 | - | Number of pixels to expand the mask edges by. Higher values produce cleaner erase results around the masked region. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`FluxProV1EraseOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<registry__image__fast_sdxl__models__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `registry__image__fast_sdxl__models__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Erase with an explicit B/W mask and an adjustable dilation (`dilate_pixels` 0-100, default 10).

**Gaps:** Price statement also charges 'Each reference megapixel ... set at 3 MP minimum' (verbatim in the price), so small images still pay the input minimum. Output uses the fast_sdxl-style Image (url/width/height/content_type).


### `fal-ai/finegrain-eraser/mask`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Finegrain Eraser Mask", category `image-to-image`, tags ["utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 with Express, $0.13 with Standard, and $0.22 with Premium." - <https://fal.ai/models/fal-ai/finegrain-eraser/mask>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.045, `is_partner_api` = true - <https://fal.ai/models/fal-ai/finegrain-eraser/mask>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/finegrain-eraser/mask>  (input schema `FinegrainEraserMaskInput`, output schema `FinegrainEraserMaskOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FinegrainEraserMaskInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to edit |
| `mask_url` | string | yes | - | - | URL of the mask image. Should be a binary mask where white (255) indicates areas to erase |
| `mode` | string | no | "standard" | ["express", "standard", "premium"] | Erase quality mode |
| `seed` | integer [minimum=0, maximum=999] \| null | no | - | - | Random seed for reproducible generation |

**OUTPUT** (`FinegrainEraserMaskOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The edited image with content erased |
| `used_seed` | integer | yes | - | - | Seed used for generation |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Erase with an explicit mask; three quality modes.

**Gaps:** Price sentence gives $0.04/$0.13/$0.22 per mode while the page billing metadata shows 0.045 per image - two different figures on the same page; use the sentence as the stated price and treat 0.045 as unreconciled. `seed` limited to 0-999.


### `fal-ai/ideogram/object-removal`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram Object Removal", category `image-to-image`, tags ["utility", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per generation." - <https://fal.ai/models/fal-ai/ideogram/object-removal>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/object-removal>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/object-removal>  (input schema `IdeogramObjectRemovalInput`, output schema `IdeogramObjectRemovalOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramObjectRemovalInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The source image containing the object to remove (maximum file size 10MB). |
| `mask_url` | string | yes | - | - | A black-and-white mask matching the source image dimensions. White pixels are removed and black pixels are preserved (maximum file size 10MB). |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`IdeogramObjectRemovalOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The image with the masked object removed. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Erase with an explicit B/W mask; no tuning parameters at all (image_url, mask_url, sync_mode).

**Gaps:** Max file size 10MB for both inputs per the descriptions.


### `fal-ai/ideogram/v3/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram V3 Edit", category `image-to-image`, tags ["realism", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with QUALITY." - <https://fal.ai/models/fal-ai/ideogram/v3/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3/edit>  (input schema `IdeogramV3EditInput`, output schema `IdeogramV3EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramV3EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_urls` | array<string> \| null | no | - | - | A set of images to use as style references (maximum total size 10MB across all style references). The images should be in JPEG, PNG or WebP format |
| `rendering_speed` | string | no | "BALANCED" | ["TURBO", "BALANCED", "QUALITY"] | The rendering speed to use. |
| `color_palette` | ColorPalette (object) \| null | no | - | - | A color palette for generation, must EITHER be specified via one of the presets (name) or explicitly via hexadecimal representations of the color with optional weights (members) |
| `style_codes` | array<string> \| null | no | - | - | A list of 8 character hexadecimal codes representing the style of the image. Cannot be used in conjunction with style_reference_images or style |
| `expand_prompt` | boolean | no | true | - | Determine if MagicPrompt should be used in generating the request or not. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `style_preset` | string \| null | no | - | ["80S_ILLUSTRATION", "90S_NOSTALGIA", "ABSTRACT_ORGANIC", "ANALOG_NOSTALGIA", "ART_BRUT", "ART_DECO", "ART_POSTER", "AURA", "AVANT_GARDE", "BAUHAUS", "BLUEPRINT", "BLURRY_MOTION", "BRIGHT_ART", "C4D_CARTOON", "CHILDRENS_BOOK", "COLLAGE", "COLORING_BOOK_I", "COLORING_BOOK_II", "CUBISM", "DARK_AURA", "DOODLE", "DOUBLE_EXPOSURE", "DRAMATIC_CINEMA", "EDITORIAL", "EMOTIONAL_MINIMAL", "ETHEREAL_PARTY", "EXPIRED_FILM", "FLAT_ART", "FLAT_VECTOR", "FOREST_REVERIE", "GEO_MINIMALIST", "GLASS_PRISM", "GOLDEN_HOUR", "GRAFFITI_I", "GRAFFITI_II", "HALFTONE_PRINT", "HIGH_CONTRAST", "HIPPIE_ERA", "ICONIC", "JAPANDI_FUSION", "JAZZY", "LONG_EXPOSURE", "MAGAZINE_EDITORIAL", "MINIMAL_ILLUSTRATION", "MIXED_MEDIA", "MONOCHROME", "NIGHTLIFE", "OIL_PAINTING", "OLD_CARTOONS", "PAINT_GESTURE", "POP_ART", "RETRO_ETCHING", "RIVIERA_POP", "SPOTLIGHT_80S", "STYLIZED_RED", "SURREAL_COLLAGE", "TRAVEL_POSTER", "VINTAGE_GEO", "VINTAGE_POSTER", "WATERCOLOR", "WEIRD", "WOODBLOCK_PRINT"] | Style preset for generation. The chosen style preset will guide the generation. |
| `prompt` | string | yes | - | - | The prompt to fill the masked part of the image. |
| `image_url` | string | yes | - | - | The image URL to generate an image from. MUST have the exact same dimensions (width and height) as the mask image. |
| `mask_url` | string | yes | - | - | The mask URL to inpaint the image. MUST have the exact same dimensions (width and height) as the input image. |

*Nested `ColorPalette` (at `color_palette`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members` | array<ColorPaletteMember (object)> \| null | no | - | - | A list of color palette members that define the color palette |
| `color_palette.name` | string \| null | no | - | ["EMBER", "FRESH", "JUNGLE", "MAGIC", "MELON", "MOSAIC", "PASTEL", "ULTRAMARINE"] | A color palette preset value |

*Nested `ColorPaletteMember` (at `color_palette.members`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb` | RGBColor (object) | yes | - | - | RGB color value for the palette member |
| `color_palette.members.color_weight` | number [minimum=0.05, maximum=1] \| null | no | 0.5 | - | The weight of the color in the color palette |

*Nested `RGBColor` (at `color_palette.members.rgb`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb.r` | integer [minimum=0, maximum=255] | no | 0 | - | Red color value |
| `color_palette.members.rgb.g` | integer [minimum=0, maximum=255] | no | 0 | - | Green color value |
| `color_palette.members.rgb.b` | integer [minimum=0, maximum=255] | no | 0 | - | Blue color value |

**OUTPUT** (`IdeogramV3EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Prompted inpaint with Ideogram v3 (good at text per the model's gallery tags) - the 'change the words on the sign' tool.

**Gaps:** Mask polarity NOT STATED; but image and mask 'MUST have the exact same dimensions'. `style_codes` cannot be combined with `style_reference_images` or `style` per the description, yet this input has no `style` property. Output `File` objects carry no width/height.


### `fal-ai/qwen-image-edit/inpaint`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Qwen Image Edit", category `image-to-image`, tags ["image-to-image", "inpainting", "qwen-image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per megapixel." - <https://fal.ai/models/fal-ai/qwen-image-edit/inpaint>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.03, `is_partner_api` = false - <https://fal.ai/models/fal-ai/qwen-image-edit/inpaint>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/qwen-image-edit/inpaint>  (input schema `QwenImageEditInpaintInput`, output schema `QwenImageEditInpaintOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`QwenImageEditInpaintInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate the image with |
| `image_size` | ImageSize (object) \| string \| null | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. |
| `num_inference_steps` | integer [minimum=2, maximum=50] | no | 30 | - | The number of inference steps to perform. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 4 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked, and images flagged as unsafe are returned as black images. |
| `output_format` | string | no | "png" | ["jpeg", "png"] | The format of the generated image. |
| `image_url` | string | yes | - | - | The URL of the image to edit. |
| `negative_prompt` | string | no | " " | - | The negative prompt for the generation |
| `acceleration` | string | no | "regular" | ["none", "regular", "high"] | Acceleration level for image generation. Options: 'none', 'regular'. Higher acceleration increases speed. 'regular' balances speed and quality. |
| `mask_url` | string | yes | - | - | The URL of the mask for inpainting |
| `strength` | number [minimum=0.01, maximum=1] | no | 0.93 | - | Strength of noising process for inpainting |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`QwenImageEditInpaintOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Prompted inpaint with Qwen-Image-Edit; `strength` controls how much of the masked area is re-noised.

**Gaps:** Mask polarity NOT STATED ('The URL of the mask for inpainting'). Price is per megapixel; `image_size` default is unset (model decides).


### `bria/genfill/v2`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Genfill", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per megapixel." - <https://fal.ai/models/bria/genfill/v2>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/bria/genfill/v2>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bria/genfill/v2>  (input schema `GenfillV2Input`, output schema `GenfillV2Output`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GenfillV2Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The source image. |
| `mask_url` | string | yes | - | - | The mask image. |
| `instruction` | string | yes | - | - | Instruct what elements you would like to fill in your image based on the mask. |
| `seed` | integer | no | 5555 | - | Random seed for reproducibility. |
| `steps_num` | integer [minimum=20, maximum=50] | no | 30 | - | Number of inference steps. |
| `sync_mode` | boolean | no | false | - | If true, returns the image directly in the response (increases latency). |

**OUTPUT** (`GenfillV2Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Generated image. |
| `images` | array<Image (object)> | no | [] | - | Generated images. |
| `vgl` | object | yes | - | - | The VGL document the image was generated from. |
| `structured_instruction` | object | yes | - | - | Deprecated, use `vgl`. The same document under the old name. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Newer Bria generative fill: takes an instruction instead of a prompt.

**Gaps:** Mask polarity NOT STATED ('The mask image.'). Output carries `vgl` and a deprecated `structured_instruction` (untyped objects). `seed` defaults to 5555 (not random). Price is per megapixel.


### `fal-ai/image-editing/object-removal`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Image Editing Object Removal", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/object-removal>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/object-removal>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/object-removal>  (input schema `ImageEditingObjectRemovalInput`, output schema `ImageEditingObjectRemovalOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingObjectRemovalInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string | no | "background people" | - | Specify which objects to remove from the image. |

**OUTPUT** (`ImageEditingObjectRemovalOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Prompt-only object removal (default prompt 'background people'); no mask.

**Gaps:** No mask input; there is no way to constrain WHICH instance is removed. `image_url` description is copy-pasted ('Image prompt for the omni model.').


### `fal-ai/image-editing/text-removal`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Image Editing Text Removal", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/text-removal>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/text-removal>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/text-removal>  (input schema `ImageEditingTextRemovalInput`, output schema `ImageEditingTextRemovalOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingTextRemovalInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image containing text to be removed. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingTextRemovalOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Removes text from an image with no instruction - the 'clean plate before re-typesetting the title' tool.

**Gaps:** No prompt and no mask: all text is targeted; cannot remove one word only.


## 3. Background replace, relight, shadows


### `fal-ai/bria/background/replace`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria Background Replace", category `image-to-image`, tags ["image editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generation." - <https://fal.ai/models/fal-ai/bria/background/replace>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/background/replace>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/background/replace>  (input schema `BriaBackgroundReplaceInput`, output schema `BriaBackgroundReplaceOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaBackgroundReplaceInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input Image to erase from |
| `ref_image_url` | string | no | "" | - | The URL of the reference image to be used for generating the new background. Use "" to leave empty. Either ref_image_url or bg_prompt has to be provided but not both. If both ref_image_url and ref_image_file are provided, ref_image_url will be used. Accepted formats are jpeg, jpg, png, webp. |
| `prompt` | string [minLength=1] \| null | no | - | - | The prompt you would like to use to generate images. |
| `negative_prompt` | string | no | "" | - | The negative prompt you would like to use to generate images. |
| `refine_prompt` | boolean | no | true | - | Whether to refine prompt |
| `seed` | integer [minimum=0, maximum=2147483647] \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `fast` | boolean | no | true | - | Whether to use the fast model |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of Images to generate. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`BriaBackgroundReplaceOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated images |
| `seed` | integer | yes | - | - | Seed value used for generation. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Replace background from a prompt or a reference image (exactly one of the two).

**Gaps:** Schema says 'Either ref_image_url or bg_prompt has to be provided but not both' but the property is named `prompt` and `ref_image_url` defaults to the empty string. `image_url` description is copy-pasted ('Input Image to erase from'). Subject-edge quality / transparent input handling not stated.


### `fal-ai/iclight-v2`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "IC-Light-v2 for Image Relighting", category `image-to-image`, tags ["relighting", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.1 per megapixel." - <https://fal.ai/models/fal-ai/iclight-v2>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.1, `is_partner_api` = false - <https://fal.ai/models/fal-ai/iclight-v2>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/iclight-v2>  (input schema `IclightV2Input`, output schema `IclightV2Output`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IclightV2Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `negative_prompt` | string | no | "" | - | Negative Prompt for the image |
| `image_url` | string | yes | - | - | URL of image to be used for relighting |
| `mask_image_url` | string \| null | no | - | - | URL of mask to be used for ic-light conditioning image |
| `background_threshold` | number [minimum=0.01, maximum=1] | no | 0.67 | - | Threshold for the background removal algorithm. A high threshold will produce sharper masks. Note: This parameter is currently deprecated and has no effect on the output. |
| `image_size` | ImageSize (object) \| string | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 28 | - | The number of inference steps to perform. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `initial_latent` | string | no | "None" | ["None", "Left", "Right", "Top", "Bottom"] | Provide lighting conditions for the model |
| `enable_hr_fix` | boolean | no | false | - | Use HR fix |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `cfg` | number [minimum=0.01, maximum=5] | no | 1 | - | The real classifier-free-guidance scale for the generation. |
| `lowres_denoise` | number [minimum=0.01, maximum=1] | no | 0.98 | - | Strength for low-resolution pass. |
| `highres_denoise` | number [minimum=0.01, maximum=1] | no | 0.95 | - | Strength for high-resolution pass. Only used if enable_hr_fix is True. |
| `hr_downscale` | number [minimum=0.01, maximum=1] | no | 0.5 | - |  |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`IclightV2Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Relight a subject from a text prompt (and optional `mask_image_url`) - rim light / colour cast on a cut-out subject.

**Gaps:** `background_threshold` is documented as deprecated with no effect. Mask polarity for `mask_image_url` NOT STATED ('URL of mask to be used for ic-light conditioning image'). `image_size` has no default. Price is per megapixel ($0.1) - the dearest of the relight options.


### `fal-ai/bria/product-shot`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria Product Shot", category `image-to-image`, tags ["product photography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generation." - <https://fal.ai/models/fal-ai/bria/product-shot>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/product-shot>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/product-shot>  (input schema `BriaProductShotInput`, output schema `BriaProductShotOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaProductShotInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the product shot to be placed in a lifestyle shot. If both image_url and image_file are provided, image_url will be used. Accepted formats are jpeg, jpg, png, webp. Maximum file size 12MB. |
| `scene_description` | string \| null | no | - | - | Text description of the new scene or background for the provided product shot. Bria currently supports prompts in English only, excluding special characters. |
| `ref_image_url` | string | no | "" | - | The URL of the reference image to be used for generating the new scene or background for the product shot. Use "" to leave empty.Either ref_image_url or scene_description has to be provided but not both. If both ref_image_url and ref_image_file are provided, ref_image_url will be used. Accepted formats are jpeg, jpg, png, webp. |
| `optimize_description` | boolean | no | true | - | Whether to optimize the scene description |
| `num_results` | integer [minimum=1, maximum=4] | no | 1 | - | The number of lifestyle product shots you would like to generate. You will get num_results x 10 results when placement_type=automatic and according to the number of required placements x num_results if placement_type=manual_placement. |
| `fast` | boolean | no | true | - | Whether to use the fast model |
| `placement_type` | string | no | "manual_placement" | ["original", "automatic", "manual_placement", "manual_padding"] | This parameter allows you to control the positioning of the product in the image. Choosing 'original' will preserve the original position of the product in the image. Choosing 'automatic' will generate results with the 10 recommended positions for the product. Choosing 'manual_placement' will allow you to select predefined positions (using the parameter 'manual_placement_selection'). Selecting 'manual_padding' will allow you to control the position and size of the image by defining the desired padding in pixels around the product. |
| `original_quality` | boolean | no | false | - | This flag is only relevant when placement_type=original. If true, the output image retains the original input image's size; otherwise, the image is scaled to 1 megapixel (1MP) while preserving its aspect ratio. |
| `shot_size` | array<integer> | no | [1000, 1000] | - | The desired size of the final product shot. For optimal results, the total number of pixels should be around 1,000,000. This parameter is only relevant when placement_type=automatic or placement_type=manual_placement. |
| `manual_placement_selection` | string | no | "bottom_center" | ["upper_left", "upper_right", "bottom_left", "bottom_right", "right_center", "left_center", "upper_center", "bottom_center", "center_vertical", "center_horizontal"] | If you've selected placement_type=manual_placement, you should use this parameter to specify which placements/positions you would like to use from the list. You can select more than one placement in one request. |
| `padding_values` | array<integer> | no | - | - | The desired padding in pixels around the product, when using placement_type=manual_padding. The order of the values is [left, right, top, bottom]. For optimal results, the total number of pixels, including padding, should be around 1,000,000. It is recommended to first use the product cutout API, get the cutout and understand the size of the result, and then define the required padding and use the cutout as an input for this API. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`BriaProductShotOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated images |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Places a product cut-out into a generated scene with controlled placement; designed for products, not people.

**Gaps:** Output count is `num_results` x 10 when `placement_type=automatic` (stated in the description) - cost could multiply; price statement is per generation. `shot_size`/`padding_values` are bare integer arrays (order stated only for padding: [left, right, top, bottom]). Max input 12MB.


### `fal-ai/image-editing/background-change`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Background Change", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/background-change>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/background-change>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/background-change>  (input schema `ImageEditingBackgroundChangeInput`, output schema `ImageEditingBackgroundChangeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingBackgroundChangeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string | no | "beach sunset with palm trees" | - | The desired background to apply. |

**OUTPUT** (`ImageEditingBackgroundChangeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Prompt-based background change without a separate cut-out step.

**Gaps:** No mask or subject input - subject preservation is whatever the model does. `image_url` description is copy-pasted ('Image prompt for the omni model.').


### `fal-ai/image-editing/relighting`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404; model page is the not-found shell (no billing metadata); not in the gallery. The gallery does list `fal-ai/image-apps-v2/relighting` (section 3) and `bria/fibo-edit/relight`.

### `fal-ai/ideogram/v3/replace-background`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram Replace Background", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with QUALITY." - <https://fal.ai/models/fal-ai/ideogram/v3/replace-background>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3/replace-background>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3/replace-background>  (input schema `IdeogramV3ReplaceBackgroundInput`, output schema `IdeogramV3ReplaceBackgroundOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramV3ReplaceBackgroundInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_urls` | array<string> \| null | no | - | - | A set of images to use as style references (maximum total size 10MB across all style references). The images should be in JPEG, PNG or WebP format |
| `rendering_speed` | string | no | "BALANCED" | ["TURBO", "BALANCED", "QUALITY"] | The rendering speed to use. |
| `color_palette` | ColorPalette (object) \| null | no | - | - | A color palette for generation, must EITHER be specified via one of the presets (name) or explicitly via hexadecimal representations of the color with optional weights (members) |
| `style_codes` | array<string> \| null | no | - | - | A list of 8 character hexadecimal codes representing the style of the image. Cannot be used in conjunction with style_reference_images or style |
| `style` | string \| null | no | - | ["AUTO", "GENERAL", "REALISTIC", "DESIGN"] | The style type to generate with. Cannot be used with style_codes. |
| `expand_prompt` | boolean | no | true | - | Determine if MagicPrompt should be used in generating the request or not. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `style_preset` | string \| null | no | - | (same 62-value list as `fal-ai/ideogram/v3/edit -> style_preset`) | Style preset for generation. The chosen style preset will guide the generation. |
| `prompt` | string | yes | - | - | Cyber punk city with neon lights and skyscrappers |
| `image_url` | string | yes | - | - | The image URL whose background needs to be replaced |

*Nested `ColorPalette` (at `color_palette`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members` | array<ColorPaletteMember (object)> \| null | no | - | - | A list of color palette members that define the color palette |
| `color_palette.name` | string \| null | no | - | ["EMBER", "FRESH", "JUNGLE", "MAGIC", "MELON", "MOSAIC", "PASTEL", "ULTRAMARINE"] | A color palette preset value |

*Nested `ColorPaletteMember` (at `color_palette.members`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb` | RGBColor (object) | yes | - | - | RGB color value for the palette member |
| `color_palette.members.color_weight` | number [minimum=0.05, maximum=1] \| null | no | 0.5 | - | The weight of the color in the color palette |

*Nested `RGBColor` (at `color_palette.members.rgb`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb.r` | integer [minimum=0, maximum=255] | no | 0 | - | Red color value |
| `color_palette.members.rgb.g` | integer [minimum=0, maximum=255] | no | 0 | - | Green color value |
| `color_palette.members.rgb.b` | integer [minimum=0, maximum=255] | no | 0 | - | Blue color value |

**OUTPUT** (`IdeogramV3ReplaceBackgroundOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Replace the background from a prompt, with style presets/palettes.

**Gaps:** The `prompt` description is a placeholder example ('Cyber punk city with neon lights and skyscrappers'), not documentation. No mask input.


### `bria/replace-background`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Replace Background", category `image-to-image`, tags ["bria", "replace-background"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/bria/replace-background>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/bria/replace-background>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bria/replace-background>  (input schema `ReplaceBackgroundInput`, output schema `ReplaceBackgroundOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ReplaceBackgroundInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string \| null | no | - | - | Prompt for background replacement. |
| `image_url` | string \| null | no | "https://v3b.fal.media/files/b/0a8bea8c/Mztgx0NG3HPdby-4iPqwH_a_coffee_machine_s... | - | Reference image (file or URL). |
| `seed` | integer | no | 4925634 | - | Random seed for reproducibility. |
| `steps_num` | integer | no | 30 | - | Number of inference steps. |
| `negative_prompt` | string | no | "" | - | Negative prompt for background replacement. |
| `sync_mode` | boolean | no | false | - | If true, returns the image directly in the response (increases latency). |

**OUTPUT** (`ReplaceBackgroundOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Generated image. |
| `images` | array<object> | no | - | - | Generated images. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Newer Bria background replace (prompt + negative prompt).

**Gaps:** `image_url` is NOT required here and has a default sample image URL - a missing image silently processes the sample. Output `images` is an untyped array.


### `fal-ai/image-apps-v2/relighting`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Relighting", category `image-to-image`, tags ["relighting"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-apps-v2/relighting>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-apps-v2/relighting>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-apps-v2/relighting>  (input schema `ImageAppsV2RelightingInput`, output schema `ImageAppsV2RelightingOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageAppsV2RelightingInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image URL for relighting |
| `lighting_style` | string | no | "natural" | ["natural", "studio", "golden_hour", "blue_hour", "dramatic", "soft", "hard", "backlight", "side_light", "front_light", "rim_light", "sunset", "sunrise", "neon", "candlelight", "moonlight", "spotlight", "ambient"] | - |
| `aspect_ratio` | AspectRatio (object) | no | - | - | Aspect ratio for 4K output |

*Nested `AspectRatio` (at `aspect_ratio`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `aspect_ratio.ratio` | string | no | "1:1" | ["1:1", "16:9", "9:16", "4:3", "3:4"] | Aspect ratio for 4K resolution output |

**OUTPUT** (`ImageAppsV2RelightingOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Image with new lighting |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Relight by choosing one of 18 named lighting styles - enum-driven, so UI can be a dropdown.

**Gaps:** `aspect_ratio` is a nested object with `ratio`, described as 'Aspect ratio for 4K resolution output'; the real output resolution is NOT STATED. `lighting_style` has no descriptions per value.


## 4. Outpaint, expand, reframe

`fal-ai/flux-pro/v1/fill` was also requested for outpaint use; it is documented in section 2. Its schema has no canvas-expansion parameters (only `image_url`, `mask_url`, `prompt`, ...), so an outpaint with it means the caller pads the image and builds the mask on the larger canvas itself (mask polarity is NOT STATED there).


### `fal-ai/bria/expand`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bria Expand Image", category `image-to-image`, tags ["outpainting"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generation." - <https://fal.ai/models/fal-ai/bria/expand>
  - Billing metadata embedded in the model page: `billing_unit` = "generations", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bria/expand>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bria/expand>  (input schema `BriaExpandInput`, output schema `BriaExpandOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BriaExpandInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the input image. |
| `canvas_size` | array<integer> | yes | - | - | The desired size of the final image, after the expansion. should have an area of less than 5000x5000 pixels. |
| `aspect_ratio` | string \| null | no | - | ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9"] | The desired aspect ratio of the final image. Will be used over original_image_size and original_image_location if provided. |
| `original_image_size` | array<integer> \| null | no | - | - | The desired size of the original image, inside the full canvas. Ensure that the ratio of input image foreground or main subject to the canvas area is greater than 15% to achieve optimal results. Will be ignored if aspect_ratio is provided. |
| `original_image_location` | array<integer> \| null | no | - | - | The desired location of the original image, inside the full canvas. Provide the location of the upper left corner of the original image. The location can also be outside the canvas (the original image will be cropped). Will be ignored if aspect_ratio is provided. |
| `prompt` | string | no | "" | - | Text on which you wish to base the image expansion. This parameter is optional. Bria currently supports prompts in English only, excluding special characters. |
| `seed` | integer [minimum=0, maximum=2147483647] \| null | no | - | - | You can choose whether you want your generated expension to be random or predictable. You can recreate the same result in the future by using the seed value of a result from the response. You can exclude this parameter if you are not interested in recreating your results. This parameter is optional. |
| `negative_prompt` | string | no | "" | - | The negative prompt you would like to use to generate images. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`BriaExpandOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image |
| `seed` | integer | yes | - | - | Seed value used for generation. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** the DEFAULT expand endpoint (`ImageExpand`). The editor always sends an explicit canvas: `canvas_size` = the finished picture, `original_image_size` = the size to draw the original at, `original_image_location` = its top-left corner (all planned by `ExpandPlanner` and kept under 25,000,000 pixels of canvas). The ORIGINAL picture is always sent - Bria draws it at the planned size, so no smaller copy is ever needed. `prompt`, `negative_prompt` and `seed` are forwarded; `aspect_ratio` is deliberately NOT forwarded (it "will be used over original_image_size and original_image_location", i.e. the vendor would choose the canvas and the placement and the editor could not put the result back on the original's pixels), nor is `sync_mode`.

**Element order (resolved 2026-10-10):** the field descriptions do not state it, but fal's own OpenAPI document for this endpoint carries examples that do: input `canvas_size` example `[1200, 674]`, `original_image_size` `[610, 855]`, `original_image_location` `[301, -66]`, and the OUTPUT example is an image with `"width": 1200, "height": 674` - so `canvas_size` is `[width, height]`; a 610-wide original at x = 301 is centred in a 1200-wide canvas (301 + 305 = 606), consistent with `[width, height]` / `[x, y]` for the other two. Bria's own API reference (<https://docs.bria.ai/image-editing/v2-endpoints/image-expansion>, read 2026-10-10) states it outright: canvas "(width, height)", original size "[width, height]", location "[x, y]". The client sends `[width, height]` and `[x, y]`.

**Gaps:** Canvas must be 'less than 5000x5000 pixels' in area (description wording; the client refuses 25,000,000 and above before sending). No input-size limit is documented (Bria's reference lists formats only). With `aspect_ratio`, Bria's reference says it centres the input and resizes it to stay under the area limit - unused here, see above.


### `fal-ai/ideogram/v3/reframe`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Ideogram", category `image-to-image`, tags ["realism", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with QUALITY." - <https://fal.ai/models/fal-ai/ideogram/v3/reframe>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3/reframe>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3/reframe>  (input schema `IdeogramV3ReframeInput`, output schema `IdeogramV3ReframeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramV3ReframeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_urls` | array<string> \| null | no | - | - | A set of images to use as style references (maximum total size 10MB across all style references). The images should be in JPEG, PNG or WebP format |
| `rendering_speed` | string | no | "BALANCED" | ["TURBO", "BALANCED", "QUALITY"] | The rendering speed to use. |
| `color_palette` | ColorPalette (object) \| null | no | - | - | A color palette for generation, must EITHER be specified via one of the presets (name) or explicitly via hexadecimal representations of the color with optional weights (members) |
| `style_codes` | array<string> \| null | no | - | - | A list of 8 character hexadecimal codes representing the style of the image. Cannot be used in conjunction with style_reference_images or style |
| `style` | string \| null | no | - | ["AUTO", "GENERAL", "REALISTIC", "DESIGN"] | The style type to generate with. Cannot be used with style_codes. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `style_preset` | string \| null | no | - | (same 62-value list as `fal-ai/ideogram/v3/edit -> style_preset`) | Style preset for generation. The chosen style preset will guide the generation. |
| `image_url` | string | yes | - | - | The image URL to reframe |
| `image_size` | ImageSize (object) \| string | yes | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The resolution for the reframed output image |

*Nested `ColorPalette` (at `color_palette`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members` | array<ColorPaletteMember (object)> \| null | no | - | - | A list of color palette members that define the color palette |
| `color_palette.name` | string \| null | no | - | ["EMBER", "FRESH", "JUNGLE", "MAGIC", "MELON", "MOSAIC", "PASTEL", "ULTRAMARINE"] | A color palette preset value |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

*Nested `ColorPaletteMember` (at `color_palette.members`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb` | RGBColor (object) | yes | - | - | RGB color value for the palette member |
| `color_palette.members.color_weight` | number [minimum=0.05, maximum=1] \| null | no | 0.5 | - | The weight of the color in the color palette |

*Nested `RGBColor` (at `color_palette.members.rgb`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb.r` | integer [minimum=0, maximum=255] | no | 0 | - | Red color value |
| `color_palette.members.rgb.g` | integer [minimum=0, maximum=255] | no | 0 | - | Green color value |
| `color_palette.members.rgb.b` | integer [minimum=0, maximum=255] | no | 0 | - | Blue color value |

**OUTPUT** (`IdeogramV3ReframeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Reframe/extend an image to a new `image_size` (e.g. 16:9 thumbnail from a square).

**Gaps:** `image_size` is REQUIRED here. How the source is placed/scaled inside the new frame is not stated. Output `File` has no width/height.


### `fal-ai/image-editing/expand`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404; model page is the not-found shell; not in the gallery. See `fal-ai/bria/expand`, `fal-ai/flux-2-pro/outpaint`, `fal-ai/image-apps-v2/outpaint` instead.

### `fal-ai/flux-2-pro/outpaint`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "FLUX 2 Pro Outpaint", category `image-to-image`, tags ["image-to-image", "outpaint", "outpainting"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 for the first megapixel of output, plus $0.015 per extra megapixel of input and output, rounded up to the nearest megapixel. For example, a 1024x1024 image will cost $0.03, and a 1920x1080 image will cost $0.045 ($0.03 for first megapixel + $0.015 for the second megapixel). Similarly, a 512x512 output will cost $0.03 ($0.03 for 0.25 megapixels, rounded to 1 megapixel)." - <https://fal.ai/models/fal-ai/flux-2-pro/outpaint>
  - Billing metadata embedded in the model page: `billing_unit` = "processed megapixels", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-2-pro/outpaint>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-2-pro/outpaint>  (input schema `Flux2ProOutpaintInput`, output schema `Flux2ProOutpaintOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Flux2ProOutpaintInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The image URL to expand using outpainting. |
| `expand_top` | integer [minimum=0, maximum=2048] | no | 0 | - | Pixels to expand at the top of the image. |
| `expand_bottom` | integer [minimum=0, maximum=2048] | no | 0 | - | Pixels to expand at the bottom of the image. |
| `expand_left` | integer [minimum=0, maximum=2048] | no | 0 | - | Pixels to expand on the left of the image. |
| `expand_right` | integer [minimum=0, maximum=2048] | no | 0 | - | Pixels to expand on the right of the image. |
| `auto_crop` | boolean | no | false | - | Whether to automatically crop the reference image when the expanded canvas does not fully contain it. |
| `mode` | string | no | "high" | ["high", "fast"] | Quality/speed trade-off for outpainting. `high` (default) delivers the highest-fidelity results; `fast` is significantly faster and extends most scenes well but may produce lower fidelity. |
| `enable_safety_checker` | boolean | no | true | - | Whether to enable the safety checker. Disabling it requires account authorization; unauthorized requests are always checked. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`Flux2ProOutpaintOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The outpainted images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** an ALTERNATIVE expand endpoint (it was the default until 2026-10-10; a row that names it keeps using it). Margins only, so the editor's "fit a shape" is turned into margins by `ExpandPlanner`. No prompt: the editor hides the prompt field when the row resolves here.

**Limits the schema does not show** (Black Forest Labs, <https://help.bfl.ai/articles/8531149640-what-are-the-resolution-limits>, read 2026-10-10): FLUX.2 "generates images up to 4MP (e.g., 2048x2048)" and "Input images over 4MP are automatically resized". The client therefore treats 4,000,000 pixels as the ceiling for both the input and the finished picture (the conservative reading of "4MP"), refusing anything larger before sending; an input BFL resized by its unstated rule would no longer match the margins. When the layer's original is over those limits the editor sends a copy at the size the layer appears in the design (or, if even that is too large, the largest copy that fits) and tells the user.

**Gaps:** Does not take a prompt, so the content of the extension is not steerable. `auto_crop` semantics are only partly described. Price scales with input+output megapixels (a 4 MP outpaint of a 2 MP copy is about $0.105; the rate card's figure is the $0.03 first-megapixel "from" price). This is why it is no longer the default: see `fal-ai/bria/expand`.


### `fal-ai/image-apps-v2/outpaint`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Image Outpaint", category `image-to-image`, tags ["outpainting"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.035 per megapixel." - <https://fal.ai/models/fal-ai/image-apps-v2/outpaint>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.035, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-apps-v2/outpaint>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-apps-v2/outpaint>  (input schema `ImageAppsV2OutpaintInput`, output schema `ImageAppsV2OutpaintOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageAppsV2OutpaintInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image URL to outpaint |
| `expand_left` | integer [minimum=0, maximum=700] | no | 0 | - | Number of pixels to add as black margin on the left side (0-700). |
| `expand_right` | integer [minimum=0, maximum=700] | no | 0 | - | Number of pixels to add as black margin on the right side (0-700). |
| `expand_top` | integer [minimum=0, maximum=700] | no | 0 | - | Number of pixels to add as black margin on the top side (0-700). |
| `expand_bottom` | integer [minimum=0, maximum=700] | no | 0 | - | Number of pixels to add as black margin on the bottom side (0-700). |
| `zoom_out_percentage` | number [minimum=0, maximum=90] | no | 20 | - | Percentage to zoom out the image. If set, the image will be scaled down by this percentage and black margins will be added to maintain original size. Example: 50 means the image will be 50% of original size with black margins filling the rest. |
| `prompt` | string [maxLength=500] | no | "" | - | Optional prompt to guide the outpainting. If provided, it will be appended to the base outpaint instruction. Example: 'with a beautiful sunset in the background' |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of images to generate. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. |
| `sync_mode` | boolean | no | false | - | If True, the function will wait for the image to be generated and uploaded before returning the response. If False, the function will return immediately and the image will be generated asynchronously. |
| `output_format` | string | no | "png" | ["png", "jpeg", "jpg", "webp"] | The format of the output image. |

**OUTPUT** (`ImageAppsV2OutpaintOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Outpainted image with extended scene |
| `seed` | integer | yes | - | - | The seed used for generation. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Outpaint by margins (0-700) or by zooming out a percentage, with optional prompt.

**Gaps:** Descriptions say margins are added 'as black margin'. `sync_mode` carries a different description from the standard one (it talks about waiting for upload). `output_format` includes both 'jpeg' and 'jpg'.


### `fal-ai/image-editing/reframe`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Image Editing Reframe", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/reframe>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/reframe>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/reframe>  (input schema `ImageEditingReframeInput`, output schema `ImageEditingReframeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingReframeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the old or damaged photo to restore. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string | no | "16:9" | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The desired aspect ratio for the reframed image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingReframeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Reframe to one of nine aspect ratios (default 16:9).

**Gaps:** `image_url` description is copy-pasted from photo restoration ('URL of the old or damaged photo to restore.'). Placement of the source in the new ratio is not stated.


### `fal-ai/smart-resize`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Smart Resize", category `image-to-image`, tags ["realism", "typography", "visual", "ads", ""], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.15 per output image (charged double for 4K outputs), plus a $0.05 minimum vision analysis fee per request. For $1.00, you can run this model 5 times at standard resolution." - <https://fal.ai/models/fal-ai/smart-resize>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.15, `is_partner_api` = true - <https://fal.ai/models/fal-ai/smart-resize>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/smart-resize>  (input schema `SmartResizeInput`, output schema `SmartResizeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`SmartResizeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the source image to resize. |
| `target_sizes` | array<string> | yes | - | - | The list of target dimensions to generate, each written as ``<width>x<height>`` (e.g. ``"1920x1080"``). One output image is produced per target size (multiplied by ``num_images_per_size``). The endpoint internally picks the smallest nano-banana-pro preset that fully covers each target so the final image is a pixel-perfect match with no upscaling. |
| `prompt` | string [maxLength=5000] | no | "" | - | Optional extra instruction that is forwarded to nano-banana-pro alongside the auto-generated resize prompt. Leave empty to let the model preserve the original image content as closely as possible. |
| `num_images_per_size` | integer [minimum=1, maximum=4] | no | 1 | - | The number of nano-banana-pro variants to produce for each target size. The response images list will contain ``len(target_sizes) * num_images_per_size`` images in total. |
| `resolution` | string | no | "1K" | ["1K", "2K", "4K"] | Hint for the minimum resolution tier to use internally. The smart-resize algorithm may pick a higher tier automatically when the requested target size is too large for this tier. This field does not affect the final output dimensions — those come from ``target_sizes``. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The output image format. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level applied to the inner nano-banana-pro /edit calls. Higher numbers loosen safety filtering. |
| `seed` | integer \| null | no | - | - | Optional random seed forwarded to the inner nano-banana-pro /edit calls for reproducibility. The same seed is used for every target size when set. |
| `sync_mode` | boolean | no | false | - | When True, the response contains data URIs in place of CDN URLs so the client can decode the images without an extra HTTP request. Adds latency for large outputs. |

**OUTPUT** (`SmartResizeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | All generated images, flattened across every target size. Order matches the order of ``results`` below: the first ``num_images_per_size`` entries correspond to ``target_sizes[0]`` and so on. |
| `description` | string | yes | - | - | The description of the generated images. |
| `results` | array<SmartResizeResult (object)> | yes | - | - | Per-target-size breakdown with the requested width/height and the nano-banana-pro (aspect_ratio, resolution) preset that was used internally. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

*Nested `SmartResizeResult` (at `results`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `results.images` | array<ImageFile (object)> | yes | - | - | The generated images. |
| `results.description` | string | yes | - | - | The description of the generated images. |
| `results.width` | integer | yes | - | - | The requested target width in pixels. |
| `results.height` | integer | yes | - | - | The requested target height in pixels. |
| `results.aspect_ratio` | string | yes | - | - | The nano-banana-pro aspect ratio that was used internally for this target size. |
| `results.resolution` | string | yes | - | - | The nano-banana-pro resolution tier that was used internally for this target size. |

**Use in the editor:** Produces exact pixel-size outputs (`target_sizes` like '1920x1080') by calling nano-banana-pro internally - one thumbnail to many platform sizes.

**Gaps:** Price: output charged per image plus a minimum vision-analysis fee; outputs multiply by `num_images_per_size`. Generative: content may change, not a crop/scale.


## 5. Upscale, enhance, face restore


### `fal-ai/clarity-upscaler`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Clarity Upscaler", category `image-to-image`, tags ["upscaling"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per megapixel." - <https://fal.ai/models/fal-ai/clarity-upscaler>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.03, `is_partner_api` = false - <https://fal.ai/models/fal-ai/clarity-upscaler>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/clarity-upscaler>  (input schema `ClarityUpscalerInput`, output schema `ClarityUpscalerOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ClarityUpscalerInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to upscale. |
| `prompt` | string | no | "masterpiece, best quality, highres" | - | The prompt to use for generating the image. Be as descriptive as possible for best results. |
| `upscale_factor` | number [minimum=1, maximum=4] | no | 2 | - | The upscale factor |
| `negative_prompt` | string | no | "(worst quality, low quality, normal quality:2)" | - | The negative prompt to use. Use it to address details that you don't want in the image. |
| `creativity` | number [minimum=0, maximum=1] | no | 0.35 | - | The creativity of the model. The higher the creativity, the more the model will deviate from the prompt. Refers to the denoise strength of the sampling. |
| `resemblance` | number [minimum=0, maximum=1] | no | 0.6 | - | The resemblance of the upscaled image to the original image. The higher the resemblance, the more the model will try to keep the original image. Refers to the strength of the ControlNet. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 4 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=4, maximum=50] | no | 18 | - | The number of inference steps to perform. |
| `seed` | integer [minimum=0] \| null | no | - | - | The same seed and the same prompt given to the same version of Stable Diffusion will output the same image every time. |
| `enable_safety_checker` | boolean | no | true | - | If set to false, the safety checker will be disabled. |

**OUTPUT** (`ClarityUpscalerOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The URL of the generated image. |
| `seed` | integer | yes | - | - | The seed used to generate the image. |
| `timings` | object | yes | - | - | The timings of the different steps in the workflow. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Diffusion upscaler with `creativity`/`resemblance` dials; priced per megapixel.

**Gaps:** Per-megapixel price - whether input or output megapixels are billed is not stated. Output dimensions not described beyond 'upscale_factor'.


### `fal-ai/aura-sr`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "AuraSR", category `image-to-image`, tags ["upscaling", "high-res"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0 per compute second." - <https://fal.ai/models/fal-ai/aura-sr>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/aura-sr>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/aura-sr>  (input schema `AuraSrInput`, output schema `AuraSrOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`AuraSrInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to upscale. |
| `upscale_factor` | integer | no | 4 | - | Upscaling factor. More coming soon. |
| `overlapping_tiles` | boolean | no | false | - | Whether to use overlapping tiles for upscaling. Setting this to true helps remove seams but doubles the inference time. |
| `checkpoint` | string | no | "v1" | ["v1", "v2"] | Checkpoint to use for upscaling. More coming soon. |

**OUTPUT** (`AuraSrOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Upscaled image |
| `timings` | object | yes | - | - | Timings for each step in the pipeline. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Fast 4x GAN upscaler.

**Gaps:** Price page shows '$0 per compute second' - real cost UNKNOWN. `upscale_factor` is an integer with no range in the schema ('More coming soon').


### `fal-ai/esrgan`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Upscale Images", category `image-to-image`, tags ["upscaling", "high-res"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.00111 per compute second" - <https://fal.ai/models/fal-ai/esrgan>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/esrgan>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/esrgan>  (input schema `EsrganInput`, output schema `EsrganOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`EsrganInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Url to input image |
| `scale` | number [minimum=1, maximum=8] | no | 2 | - | Rescaling factor |
| `tile` | integer | no | 0 | - | Tile size. Default is 0, that is no tile. When encountering the out-of-GPU-memory issue, please specify it, e.g., 400 or 200 |
| `face` | boolean | no | false | - | Upscaling a face |
| `model` | string | no | "RealESRGAN_x4plus" | ["RealESRGAN_x4plus", "RealESRGAN_x2plus", "RealESRGAN_x4plus_anime_6B", "RealESRGAN_x4_v3", "RealESRGAN_x4_wdn_v3", "RealESRGAN_x4_anime_v3"] | Model to use for upscaling |
| `output_format` | string | no | "png" | ["png", "jpeg"] | Output image format (png or jpeg) |

**OUTPUT** (`EsrganOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Upscaled image |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Real-ESRGAN family (incl. anime models) with scale 1-8; the only one billed in the page text by compute second.

**Gaps:** Price statement: '$0.00111 per compute second' - cost depends on runtime, not on image size; page billing metadata shows price 0 for the same endpoint (unreconciled).


### `fal-ai/creative-upscaler`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Creative Upscaler", category `image-to-image`, tags ["upscaling"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0 per compute second." - <https://fal.ai/models/fal-ai/creative-upscaler>
  - Billing metadata embedded in the model page: `billing_unit` = "compute seconds", `price` = 0, `is_partner_api` = false - <https://fal.ai/models/fal-ai/creative-upscaler>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/creative-upscaler>  (input schema `CreativeUpscalerInput`, output schema `CreativeUpscalerOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`CreativeUpscalerInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `model_type` | string | no | "SD_1_5" | ["SD_1_5", "SDXL"] | The type of model to use for the upscaling. Default is SD_1_5 |
| `image_url` | string | yes | - | - | The image to upscale. |
| `prompt` | string \| null | no | - | - | The prompt to use for generating the image. Be as descriptive as possible for best results. If no prompt is provide BLIP2 will be used to generate a prompt. |
| `scale` | number [minimum=1, maximum=5] | no | 2 | - | The scale of the output image. The higher the scale, the bigger the output image will be. |
| `creativity` | number [minimum=0, maximum=1] | no | 0.5 | - | How much the output can deviate from the original |
| `detail` | number [minimum=0, maximum=5] | no | 1 | - | How much detail to add |
| `shape_preservation` | number [minimum=0, maximum=3] | no | 0.25 | - | How much to preserve the shape of the original image |
| `prompt_suffix` | string | no | " high quality, highly detailed, high resolution, sharp" | - | The suffix to add to the prompt. This is useful to add a common ending to all prompts such as 'high quality' etc or embedding tokens. |
| `negative_prompt` | string | no | "blurry, low resolution, bad, ugly, low quality, pixelated, interpolated, compre... | - | The negative prompt to use.Use it to address details that you don't want in the image. This could be colors, objects, scenery and even the small details (e.g. moustache, blurry, low resolution). |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of Stable Diffusion will output the same image every time. |
| `guidance_scale` | number [minimum=0, maximum=16] | no | 7.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=200] | no | 20 | - | The number of inference steps to use for generating the image. The more steps the better the image will be but it will also take longer to generate. |
| `enable_safety_checks` | boolean | no | true | - | If set to true, the resulting image will be checked whether it includes any potentially unsafe content. If it does, it will be replaced with a black image. |
| `skip_ccsr` | boolean | no | false | - | If set to true, the image will not be processed by the CCSR model before being processed by the creativity model. |
| `override_size_limits` | boolean | no | false | - | Allow for large uploads that could take a very long time. |
| `base_model_url` | string \| null | no | - | - | The URL to the base model to use for the upscaling |
| `additional_lora_url` | string | no | - | - | The URL to the additional LORA model to use for the upscaling. Default is None |
| `additional_lora_scale` | number | no | 1 | - | The scale of the additional LORA model to use for the upscaling. Default is 1.0 |
| `additional_embedding_url` | string | no | - | - | The URL to the additional embeddings to use for the upscaling. Default is None |

**OUTPUT** (`CreativeUpscalerOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image file info. |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Creative (hallucinating) upscaler with LoRA/embedding hooks.

**Gaps:** Price page shows '$0 per compute second' - real cost UNKNOWN. `base_model_url`, `additional_lora_url`, `additional_embedding_url` accept arbitrary URLs - do not pass user-controlled values through.


### `fal-ai/recraft/upscale/crisp`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Recraft Crisp Upscale", category `image-to-image`, tags ["upscaling"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.004 per image." - <https://fal.ai/models/fal-ai/recraft/upscale/crisp>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.004, `is_partner_api` = true - <https://fal.ai/models/fal-ai/recraft/upscale/crisp>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/recraft/upscale/crisp>  (input schema `RecraftUpscaleCrispInput`, output schema `RecraftUpscaleCrispOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`RecraftUpscaleCrispInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to be upscaled. Must be in PNG format. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | false | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |

**OUTPUT** (`RecraftUpscaleCrispOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The upscaled image. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Cheapest fixed-price upscaler found ($0.004/image).

**Gaps:** Input MUST be PNG ('Must be in PNG format'). Output `File` has no width/height and the scale factor is NOT STATED. `enable_safety_checker` defaults to false.


### `fal-ai/recraft/upscale/creative`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Recraft Creative Upscale", category `image-to-image`, tags ["upscaling"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.25 per image." - <https://fal.ai/models/fal-ai/recraft/upscale/creative>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.25, `is_partner_api` = true - <https://fal.ai/models/fal-ai/recraft/upscale/creative>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/recraft/upscale/creative>  (input schema `RecraftUpscaleCreativeInput`, output schema `RecraftUpscaleCreativeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`RecraftUpscaleCreativeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to be upscaled. Must be in PNG format. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | false | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |

**OUTPUT** (`RecraftUpscaleCreativeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The upscaled image. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Creative upscale at $0.25/image.

**Gaps:** Same PNG-only input and unstated scale as crisp.


### `fal-ai/codeformer`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "CodeFormer", category `image-to-image`, tags ["image-restoration", "faces", "utility"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.0021 per megapixel." - <https://fal.ai/models/fal-ai/codeformer>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.0021, `is_partner_api` = false - <https://fal.ai/models/fal-ai/codeformer>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/codeformer>  (input schema `CodeformerInput`, output schema `CodeformerOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`CodeformerInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of image to be used for relighting |
| `fidelity` | number | no | 0.5 | - | Weight of the fidelity factor. |
| `upscale_factor` | number [minimum=1] | no | 2 | - | Upscaling factor |
| `aligned` | boolean | no | false | - | Should faces etc should be aligned. |
| `only_center_face` | boolean | no | false | - | Should only center face be restored |
| `face_upscale` | boolean | no | true | - | Should faces be upscaled |
| `seed` | integer \| null | no | - | - | Random seed for reproducible generation. |

**OUTPUT** (`CodeformerOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | The generated image file info. |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |

*Nested `Image` (at `image`)*: the standard fal file object (identical to the Common objects section at the top of this file: url (required), content_type, file_name, file_size, width, height — all optional).

**Use in the editor:** Face restoration (+ optional upscaling) - for faces in low-quality screenshots.

**Gaps:** `image_url` description is copy-pasted ('URL of image to be used for relighting'). `fidelity` has no range in the schema.


### `fal-ai/image-editing/face-enhancement`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Face Enhancement", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/face-enhancement>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/face-enhancement>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/face-enhancement>  (input schema `ImageEditingFaceEnhancementInput`, output schema `ImageEditingFaceEnhancementOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingFaceEnhancementInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingFaceEnhancementOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Prompt-less face enhancement.

**Gaps:** Generative: may alter identity; `image_url` description is copy-pasted ('Image prompt for the omni model.').


### `fal-ai/topaz/upscale/image`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: not present in the image-to-image / text-to-image listings.
- Price:
  - Page statement (verbatim): "For a single image, your request will cost $0.08 for up to 24MP , $0.16 for up to 48MP , $0.32 for up to 96MP , and up to $1.36 for 512MP output resolution." - <https://fal.ai/models/fal-ai/topaz/upscale/image>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/fal-ai/topaz/upscale/image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/topaz/upscale/image>  (input schema `TopazUpscaleImageInput`, output schema `TopazUpscaleImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`TopazUpscaleImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string [minLength=1] | yes | - | - | Url of the image to be upscaled |
| `model` | string | no | "Standard V2" | ["Standard V2", "High Fidelity V2", "Low Resolution V2", "CGI", "Text Refine", "Wonder 3", "Wonder", "Standard MAX", "Redefine", "Recovery V2", "Recovery"] | Model to use for image enhancement, grouped by family. Precision — Standard V2 fits most photos, High Fidelity V2 preserves detail in professional shots, Low Resolution V2 recovers compressed sources, CGI targets art and rendered graphics, Text Refine keeps text and shapes crisp. Generative — Wonder 3 is Topaz's most advanced generative model, Redefine adds prompt-guided creative detail, Standard MAX maximizes quality, Recovery/Recovery V2 rebuild extreme low-resolution images. |
| `upscale_factor` | number [minimum=1, maximum=4] | no | 2 | - | Factor to upscale the image by (e.g. 2.0 doubles width and height) |
| `crop_to_fill` | boolean | no | false | - | - |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | Output format of the upscaled image. |
| `subject_detection` | string | no | "All" | ["All", "Foreground", "Background"] | Subject detection mode for the image enhancement. Applies to standard enhance and Recovery V2 models. |
| `face_enhancement` | boolean | no | true | - | Whether to apply face enhancement to the image. Applies to standard enhance and Recovery V2 models. |
| `face_enhancement_creativity` | number [minimum=0, maximum=1] | no | 0 | - | Creativity level for face enhancement. 0.0 means no creativity, 1.0 means maximum creativity. Ignored if face enhancement is disabled. |
| `face_enhancement_strength` | number [minimum=0, maximum=1] | no | 0.8 | - | Strength of the face enhancement. 0.0 means no enhancement, 1.0 means maximum enhancement. Ignored if face enhancement is disabled. |
| `sharpen` | number [minimum=0, maximum=1] \| null | no | - | - | Sharpening level (0.0-1.0). Applies to Standard V2, Low Resolution V2, CGI, High Fidelity V2, Text Refine, and Redefine models. |
| `denoise` | number [minimum=0, maximum=1] \| null | no | - | - | Denoising level (0.0-1.0). Applies to Standard V2, Low Resolution V2, CGI, High Fidelity V2, Text Refine, and Redefine models. |
| `fix_compression` | number [minimum=0, maximum=1] \| null | no | - | - | Compression artifact removal level (0.0-1.0). Applies to Standard V2, Low Resolution V2, High Fidelity V2, and Text Refine models. |
| `strength` | number [minimum=0.01, maximum=1] \| null | no | - | - | Enhancement strength (0.01-1.0). Applies to Text Refine model only. |
| `creativity` | integer [minimum=1, maximum=6] \| null | no | - | - | Creativity level for generative upscaling (1-6). Higher values produce more creative/hallucinated details. Applies to Redefine model only. |
| `texture` | integer [minimum=1, maximum=5] \| null | no | - | - | Texture detail level for generative upscaling (1-5). Applies to Redefine model only. |
| `prompt` | string [maxLength=1024] \| null | no | - | - | Text prompt to guide generative upscaling (max 1024 chars). Applies to Redefine model only. |
| `autoprompt` | boolean \| null | no | - | - | Enable automatic prompt generation for generative upscaling. Applies to Redefine model only. |
| `detail` | number [minimum=0, maximum=1] \| null | no | - | - | Detail recovery level (0.0-1.0). Applies to Recovery V2 model only. |
| `enhancement_strength` | string \| null | no | - | ["low", "medium", "high"] | Enhancement strength for generative upscaling. Applies to Wonder 3 model only. When omitted, Topaz auto-configures it. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`TopazUpscaleImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The upscaled image. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Topaz upscaler with many models; 'Text Refine' targets text and shapes.

**Gaps:** Absent from the public gallery listing though the schema and page exist. Many parameters apply to only specific models (stated per description). Price tiers are by OUTPUT megapixels.


### `topaz/upscale/image/precision`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Topaz Upscale Image Precision", category `image-to-image`, tags ["upscale", "image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.08 per started 24 megapixels of output for every precision model. For example, an output up to 24 MP costs $0.08, over 24 MP and up to 48 MP costs $0.16, and over 48 MP and up to 72 MP costs $0.24." - <https://fal.ai/models/topaz/upscale/image/precision>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/topaz/upscale/image/precision>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=topaz/upscale/image/precision>  (input schema `UpscaleImagePrecisionInput`, output schema `UpscaleImagePrecisionOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`UpscaleImagePrecisionInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string [minLength=1] | yes | - | - | Url of the image to be upscaled |
| `model` | string | no | "Standard V2" | ["Standard V2", "High Fidelity V3", "High Fidelity V2", "Low Resolution V2", "CGI", "Text Refine", "Faces"] | Precision upscaling model. Standard V2 fits most photos; High Fidelity V3/V2 preserve detail in professional shots; Low Resolution V2 recovers compressed sources; CGI targets art and rendered graphics; Text Refine keeps text and shapes crisp; Faces restores facial detail with Face Recovery 3. |
| `upscale_factor` | number [minimum=1, maximum=4] | no | 2 | - | Factor to upscale the image by (e.g. 2.0 doubles width and height) |
| `crop_to_fill` | boolean | no | false | - | - |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | Output format of the upscaled image. |
| `subject_detection` | string | no | "All" | ["All", "Foreground", "Background"] | Subject detection mode for the image enhancement. |
| `face_enhancement` | boolean | no | true | - | Whether to apply face enhancement to the image. |
| `face_enhancement_creativity` | number [minimum=0, maximum=1] | no | 0 | - | Creativity level for face enhancement. 0.0 means no creativity, 1.0 means maximum creativity. Ignored if face enhancement is disabled. |
| `face_enhancement_strength` | number [minimum=0, maximum=1] | no | 0.8 | - | Strength of the face enhancement. 0.0 means no enhancement, 1.0 means maximum enhancement. Ignored if face enhancement is disabled. |
| `sharpen` | number [minimum=0, maximum=1] \| null | no | - | - | Sharpening level (0.0-1.0). Default varies by model. |
| `denoise` | number [minimum=0, maximum=1] \| null | no | - | - | Denoising level (0.0-1.0). Default varies by model. |
| `fix_compression` | number [minimum=0, maximum=1] \| null | no | - | - | Compression artifact removal level (0.0-1.0). Not supported by the CGI model. |
| `strength` | number [minimum=0.01, maximum=1] \| null | no | - | - | Enhancement strength (0.01-1.0). Applies to Text Refine model only. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`UpscaleImagePrecisionOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The upscaled image. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Topaz 'precision' models only (incl. Text Refine and Faces); cheapest tier of the Topaz family.

**Gaps:** Price is per started 24 megapixels of output.


### `topaz/upscale/image/transparent`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Topaz Upscale Image Transparent", category `image-to-image`, tags ["upscale", "image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.08 per started 24 megapixels of output. For example, 1024×1024 becomes 4096×4096 and costs $0.08; 2000×2000 becomes 8000×8000 and costs $0.24; 3000×2000 becomes 12000×8000 and costs $0.32." - <https://fal.ai/models/topaz/upscale/image/transparent>
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Transparent Upscale always produces a fixed 4× enlargement in width and height (16× the source pixel area) and preserves the alpha channel in PNG. Your request will cost $0.08 per started 24 megapixels of output. For example, 1024×1024 becomes 4096×4096 and costs $0.08; 2000×2000 becomes 8000×8000 and costs $0.24; 3000×2000 becomes 12000×8000 and costs $0.32." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/topaz/upscale/image/transparent>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=topaz/upscale/image/transparent>  (input schema `UpscaleImageTransparentInput`, output schema `UpscaleImageTransparentOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`UpscaleImageTransparentInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string [minLength=1] | yes | - | - | Url of the image to process |
| `output_format` | string | no | "png" | - | Transparent upscaling always outputs PNG to preserve the alpha channel. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`UpscaleImageTransparentOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The upscaled image. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Upscales PNG with alpha preserved - for upscaling cut-out layers without destroying transparency.

**Gaps:** Always 4x width and height (16x pixels) and PNG per the price text; output dimensions can get large (cost per started 24MP).


### `fal-ai/seedvr/upscale/image`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "SeedVR2", category `image-to-image`, tags ["upscale", "image-to-image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.001 per megapixel." - <https://fal.ai/models/fal-ai/seedvr/upscale/image>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.001, `is_partner_api` = false - <https://fal.ai/models/fal-ai/seedvr/upscale/image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/seedvr/upscale/image>  (input schema `SeedvrUpscaleImageInput`, output schema `SeedvrUpscaleImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`SeedvrUpscaleImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The input image to be processed |
| `upscale_mode` | string | no | "factor" | ["target", "factor"] | The mode to use for the upscale. If 'target', the upscale factor will be calculated based on the target resolution. If 'factor', the upscale factor will be used directly. |
| `upscale_factor` | number [minimum=1, maximum=10] | no | 2 | - | Upscaling factor to be used. Will multiply the dimensions with this factor when `upscale_mode` is `factor`. |
| `target_resolution` | string | no | "1080p" | ["720p", "1080p", "1440p", "2160p"] | The target resolution to upscale to when `upscale_mode` is `target`. |
| `seed` | integer \| null | no | - | - | The random seed used for the generation process. |
| `noise_scale` | number [minimum=0, maximum=1, multipleOf=0.001] | no | 0.1 | - | The noise scale to use for the generation process. |
| `output_format` | string | no | "jpg" | ["png", "jpg", "webp"] | The format of the output image. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`SeedvrUpscaleImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | ImageFile (object) | yes | - | - | Upscaled image file after processing |
| `seed` | integer | yes | - | - | The random seed used for the generation process. |

*Nested `ImageFile` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `image.width` | integer \| null | no | - | - | The width of the image |
| `image.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Upscale by factor (1-10) or to a target resolution (720p-2160p); very cheap per megapixel.

**Gaps:** Price statement is per megapixel without saying input or output megapixels.


## 6. Instruction-based image editing


### `fal-ai/flux-pro/kontext`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX.1 Kontext [pro]", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/flux-pro/kontext>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/kontext>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/kontext>  (input schema `FluxProKontextInput`, output schema `FluxProKontextOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FluxProKontextInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `guidance_scale` | number [minimum=1, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enhance_prompt` | boolean | no | false | - | Whether to enhance the prompt for better results. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `image_url` | string [minLength=1] | yes | - | - | Image prompt for the omni model. |

**OUTPUT** (`FluxProKontextOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<fal__toolkit__image__image__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `fal__toolkit__image__image__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Instruction-based single-image edit ('make the sky purple'); no mask input; fixed price per image.

**Gaps:** `image_url` description is copy-pasted ('Image prompt for the omni model.'). `safety_tolerance` text says max 5 but enum allows '6'. Whether the edit stays inside a region cannot be constrained.


### `fal-ai/flux-pro/kontext/max`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX.1 Kontext [max]", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.08 per image." - <https://fal.ai/models/fal-ai/flux-pro/kontext/max>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.08, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/kontext/max>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/kontext/max>  (input schema `FluxProKontextMaxInput`, output schema `FluxProKontextMaxOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FluxProKontextMaxInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `guidance_scale` | number [minimum=1, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enhance_prompt` | boolean | no | false | - | Whether to enhance the prompt for better results. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `image_url` | string [minLength=1] | yes | - | - | Image prompt for the omni model. |

**OUTPUT** (`FluxProKontextMaxOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<fal__toolkit__image__image__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `fal__toolkit__image__image__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Higher-quality tier of Kontext at twice the price.

**Gaps:** Same schema as Kontext; quality difference not measurable from schemas.


### `fal-ai/nano-banana/edit`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Nano Banana", category `image-to-image`, tags ["image-editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.039 per image. For $1.00, you can run this model 25 times." - <https://fal.ai/models/fal-ai/nano-banana/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.0398, `is_partner_api` = true - <https://fal.ai/models/fal-ai/nano-banana/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/nano-banana/edit>  (input schema `NanoBananaEditInput`, output schema `NanoBananaEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`NanoBananaEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The prompt for image editing. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "auto" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16"] | The aspect ratio of the generated image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use for image-to-image generation or image editing. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate. |

**OUTPUT** (`NanoBananaEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Instruction edit with reference images (`image_urls`, required); cheapest instruction editor found that takes several inputs.

**Gaps:** No mask input and no resolution parameter (only `aspect_ratio` incl. 'auto'). Output includes a `description` text. `limit_generations` is flagged experimental. Output dimensions are not stated as equal to the input.


### `fal-ai/gemini-25-flash-image/edit`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Gemini 2.5 Flash Image", category `image-to-image`, tags ["image-editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.039 per image. For $1.00, you can run this model 25 times." - <https://fal.ai/models/fal-ai/gemini-25-flash-image/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.0398, `is_partner_api` = true - <https://fal.ai/models/fal-ai/gemini-25-flash-image/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/gemini-25-flash-image/edit>  (input schema `Gemini25FlashImageEditInput`, output schema `Gemini25FlashImageEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Gemini25FlashImageEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The prompt for image editing. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "auto" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16"] | The aspect ratio of the generated image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use for image-to-image generation or image editing. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate. |

**OUTPUT** (`Gemini25FlashImageEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Same schema and same price statement as nano-banana/edit; the schema does not say whether it is the same model.

**Gaps:** Do not assume identical behaviour - only the schema is identical.


### `fal-ai/qwen-image-edit`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Qwen Image Edit", category `image-to-image`, tags ["image-editing", "image-to-image", "high-quality-text"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per megapixel." - <https://fal.ai/models/fal-ai/qwen-image-edit>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.03, `is_partner_api` = false - <https://fal.ai/models/fal-ai/qwen-image-edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/qwen-image-edit>  (input schema `QwenImageEditInput`, output schema `QwenImageEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`QwenImageEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate the image with |
| `image_size` | ImageSize (object) \| string \| null | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. |
| `num_inference_steps` | integer [minimum=2, maximum=50] | no | 30 | - | The number of inference steps to perform. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 4 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked, and images flagged as unsafe are returned as black images. |
| `output_format` | string | no | "png" | ["jpeg", "png"] | The format of the generated image. |
| `image_url` | string | yes | - | - | The URL of the image to edit. |
| `negative_prompt` | string | no | " " | - | The negative prompt for the generation |
| `acceleration` | string | no | "regular" | ["none", "regular", "high"] | Acceleration level for image generation. Options: 'none', 'regular'. Higher acceleration increases speed. 'regular' balances speed and quality. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`QwenImageEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** Instruction edit via Qwen-Image-Edit (gallery tags: image-editing, high-quality-text).

**Gaps:** Price per megapixel. `image_size` default unset. Output `Image` uses the fast_sdxl-style object (no file_size).


### `fal-ai/bytedance/seedream/v4/edit`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bytedance Seedream V4 Edit", category `image-to-image`, tags ["stylized", "transform", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per image." - <https://fal.ai/models/fal-ai/bytedance/seedream/v4/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bytedance/seedream/v4/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bytedance/seedream/v4/edit>  (input schema `BytedanceSeedreamV4EditInput`, output schema `BytedanceSeedreamV4EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BytedanceSeedreamV4EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The text prompt used to edit the image |
| `image_size` | ImageSize (object) \| string | no | {"width": 2048, "height": 2048} | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto", "auto_2K", "auto_4K"] | The size of the generated image. The minimum total image area is 921600 pixels. Failing this, the image size will be adjusted to by scaling it up, while maintaining the aspect ratio. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | Number of separate model generations to be run with the prompt. |
| `max_images` | integer [minimum=1, maximum=6] | no | 1 | - | If set to a number greater than one, enables multi-image generation. The model will potentially return up to `max_images` images every generation, and in total, `num_images` generations will be carried out. In total, the number of images generated will be between `num_images` and `max_images*num_images`. The total number of images (image inputs + image outputs) must not exceed 15 |
| `seed` | integer \| null | no | - | - | Random seed to control the stochasticity of image generation. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `enhance_prompt_mode` | string | no | "standard" | ["standard", "fast"] | The mode to use for enhancing prompt enhancement. Standard mode provides higher quality results but takes longer to generate. Fast mode provides average quality results but takes less time to generate. |
| `image_urls` | array<string> | yes | - | - | List of URLs of input images for editing. Presently, up to 10 image inputs are allowed. If over 10 images are sent, only the last 10 will be used. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`BytedanceSeedreamV4EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated images |
| `seed` | integer | yes | - | - | Seed used for generation |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Instruction edit with up to 10 inputs and up to 4K output sizes.

**Gaps:** `image_size` default is 2048x2048 square (not the input size) unless 'auto' is chosen; 'minimum total image area is 921600 pixels' - small inputs get scaled up.


### `fal-ai/flux-2/edit`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX 2 Edit", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Requests cost $0.012 per megapixel of input and output. Input images will be resized to 1MP. For example, a 1024x1024 generation with 512*512 size image input will cost 0.024 (1 MP input + 1 MP output, each costing 0.012)." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "processed megapixels", `price` = 0.012, `is_partner_api` = false - <https://fal.ai/models/fal-ai/flux-2/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-2/edit>  (input schema `Flux2EditInput`, output schema `Flux2EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Flux2EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to edit the image. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 2.5 | - | Guidance Scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `seed` | integer \| null | no | - | - | The seed to use for the generation. If not provided, a random seed will be used. |
| `num_inference_steps` | integer [minimum=4, maximum=50] | no | 28 | - | The number of inference steps to perform. |
| `image_size` | ImageSize (object) \| string \| null | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the image to generate. The width and height must be between 512 and 2048 pixels. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `acceleration` | string | no | "regular" | ["none", "regular", "high"] | The acceleration level to use for the image generation. |
| `enable_prompt_expansion` | boolean | no | false | - | If set to true, the prompt will be expanded for better results. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked, and images flagged as unsafe are returned as black images. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `image_urls` | array<string> | yes | - | - | The URLs of the images for editing. A maximum of 4 images are allowed, if more are provided, only the first 4 will be used. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`Flux2EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** FLUX.2 instruction edit, up to 4 input images.

**Gaps:** Price sentence is only in the gallery listing (page lacked the standard sentence). Pricing is per megapixel of input AND output; page billing unit 'processed megapixels'. `image_size` between 512 and 2048 per description.


### `fal-ai/gpt-image-1/edit-image`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "gpt-image-1", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost different amounts based on the input and output. You will be charged $0.002 per 1,000 input text tokens. One word is roughly 4 tokens. You will be charged $0.005 per 1,000 input image tokens. One 1024x1024 image is roughly 135 tokens in low fidelity mode, or 3,050 tokens in high fidelity mode. For" - <https://fal.ai/models/fal-ai/gpt-image-1/edit-image>
  - Billing metadata embedded in the model page: `billing_unit` = "credits", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/fal-ai/gpt-image-1/edit-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/gpt-image-1/edit-image>  (input schema `GptImage1EditImageInput`, output schema `GptImage1EditImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GptImage1EditImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=2] | yes | - | - | The prompt for image generation |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use as a reference for the generation. |
| `image_size` | string | no | "auto" | ["auto", "1024x1024", "1536x1024", "1024x1536"] | Aspect ratio for the generated image |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `quality` | string | no | "auto" | ["auto", "low", "medium", "high"] | Quality for the generated image |
| `input_fidelity` | string | no | "high" | ["low", "high"] | Input fidelity for the generated image |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of images to generate |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

**OUTPUT** (`GptImage1EditImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** OpenAI image edit via fal; `background: transparent` option; no mask parameter in this version.

**Gaps:** Token-based multi-component price (see price); real cost depends on quality and size. No `mask` property - use gpt-image-1.5/edit for a mask. Page billing metadata unit 'credits', price 1 (not meaningful).


### `fal-ai/nano-banana-pro/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Nano Banana Pro", category `image-to-image`, tags ["realism", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.15 per image. For $1.00, you can run this model 7 times. 4K outputs will be charged at double the standard rate. If web search is used, an additional $0.015 will be charged. Note: Pricing may change in the future." - <https://fal.ai/models/fal-ai/nano-banana-pro/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.15, `is_partner_api` = true - <https://fal.ai/models/fal-ai/nano-banana-pro/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/nano-banana-pro/edit>  (input schema `NanoBananaProEditInput`, output schema `NanoBananaProEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`NanoBananaProEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The prompt for image editing. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "auto" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16"] | The aspect ratio of the generated image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use for image-to-image generation or image editing. |
| `system_prompt` | string [maxLength=50000] | no | "" | - | Optional system instruction that steers the model's persona and output style across the request. Leave blank to omit; when provided, it is sent as the system instruction to Gemini (or as a system message on OpenAI-compatible providers). |
| `resolution` | string | no | "1K" | ["1K", "2K", "4K"] | The resolution of the image to generate. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate. |
| `enable_web_search` | boolean | no | false | - | Enable web search for the image generation task. This will allow the model to use the latest information from the web to generate the image. |

**OUTPUT** (`NanoBananaProEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Higher-quality nano-banana edit with 1K/2K/4K `resolution`; gallery tags it for typography.

**Gaps:** Mask not supported. 4K costs double and web search an extra fee per the price sentence. `system_prompt` is forwarded to Gemini.


### `fal-ai/nano-banana-2/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Nano Banana 2", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.08 per image. For $1.00, you can run this model 12 times. 2K and 4K outputs will be charged at 1.5 times and 2 times the standard rate, respectively. 0.5K (512px) resolution outputs will be charged at 0.75 times the standard rate. If web search is used, an additional $0.015 will be charged. If high thinking is used, an additional $0.002 will be charged. Note: Pricing is subject to change." - <https://fal.ai/models/fal-ai/nano-banana-2/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.08, `is_partner_api` = true - <https://fal.ai/models/fal-ai/nano-banana-2/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/nano-banana-2/edit>  (input schema `NanoBanana2EditInput`, output schema `NanoBanana2EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`NanoBanana2EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The prompt for image editing. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "auto" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16", "4:1", "1:4", "8:1", "1:8"] | The aspect ratio of the generated image. Supports extreme ratios: 4:1, 1:4, 8:1, 1:8. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | no | - | - | The URLs of the images to use for image-to-image generation or image editing. Optional when at least one of `video_url`, `audio_url`, or `pdf_url` is provided. |
| `system_prompt` | string [maxLength=50000] | no | "" | - | Optional system instruction that steers the model's persona and output style across the request. Leave blank to omit; when provided, it is sent as the system instruction to Gemini (or as a system message on OpenAI-compatible providers). |
| `resolution` | string | no | "1K" | ["0.5K", "1K", "2K", "4K"] | The resolution of the image to generate. |
| `video_url` | string \| null | no | - | - | Optional video to use as input context. Accepts an http(s)/data URL to a video file (downloaded and sent inline, max 15MB) or a YouTube URL (passed through to the model without downloading). |
| `audio_url` | string \| null | no | - | - | Optional audio file to use as input context. Accepts an http(s)/data URL (downloaded and sent inline, max 15MB). |
| `pdf_url` | string \| null | no | - | - | Optional PDF document to use as input context. Accepts an http(s)/data URL (downloaded and sent inline, max 15MB). |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate and ignore any intermediate images generated by the model. This may affect generation quality. |
| `enable_web_search` | boolean | no | false | - | Enable web search for the image generation task. This will allow the model to use the latest information from the web to generate the image. |
| `thinking_level` | string \| null | no | - | ["minimal", "high"] | When set, enables model thinking with the given level ('minimal' or 'high') and includes thoughts in the generation. Omit to disable. |

**OUTPUT** (`NanoBanana2EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Newer nano-banana edit with optional `thinking_level`, extra aspect ratios (4:1 ... 1:8) and 0.5K resolution.

**Gaps:** `image_urls` is NOT required here (can edit from video/audio/pdf context).


### `google/nano-banana-2.1/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Nano Banana 2.1 Edit", category `image-to-image`, tags ["image editing", "image to image", "photorealism", "typography", "text rendering"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text/thinking tokens (per 1M): $1.764 input, $8.823 output. Image tokens (per 1M): $1.764 input, $35.294 output. Changing the resolution parameter significantly affects cost; by default we use 1K. See the description at the bottom of this page for more details on how much canonical image sizes cost." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/google/nano-banana-2.1/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=google/nano-banana-2.1/edit>  (input schema `NanoBanana21EditInput`, output schema `NanoBanana21EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`NanoBanana21EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The prompt for image editing. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "auto" | (same 15-value list as `fal-ai/nano-banana-2/edit -> aspect_ratio`) | The aspect ratio of the generated image. Supports extreme ratios: 4:1, 1:4, 8:1, 1:8. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | no | - | - | The URLs of the images to use for image-to-image generation or image editing. Optional when `video_url` or `pdf_url` is provided. |
| `system_prompt` | string [maxLength=50000] | no | "" | - | Optional system instruction that steers the model's persona and output style across the request. Leave blank to omit; when provided, it is sent as the system instruction to Gemini (or as a system message on OpenAI-compatible providers). |
| `resolution` | string | no | "1K" | ["1K", "2K", "4K"] | The resolution of the image to generate. |
| `video_url` | string \| null | no | - | - | Optional video to use as input context. Accepts an http(s)/data URL to a video file (downloaded and sent inline, max 15MB) or a YouTube URL (passed through to the model without downloading). |
| `pdf_url` | string \| null | no | - | - | Optional PDF document to use as input context. Accepts an http(s)/data URL (downloaded and sent inline, max 15MB). |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate and ignore any intermediate images generated by the model. This may affect generation quality. |
| `enable_web_search` | boolean | no | false | - | Enable web search for the image generation task. This will allow the model to use the latest information from the web to generate the image. |
| `thinking_level` | string \| null | no | "medium" | ["minimal", "medium", "high"] | How much the model reasons before generating. Higher levels can improve complex compositions and use more thinking tokens. |

**OUTPUT** (`NanoBanana21EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The edited images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Token-priced nano-banana 2.1 edit.

**Gaps:** Token-based price (see price) - cost per edit depends on `resolution` and `thinking_level`; not a flat per-image price.


### `fal-ai/flux-2-pro/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "FLUX 2 Pro Edit", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 for the first megapixel of output, plus $0.015 per extra megapixel of input and output, rounded up to the nearest megapixel. For example, a 1024x1024 image will cost $0.03, and a 1920x1080 image will cost $0.045 ($0.03 for first megapixel + $0.015 for the second megapixel). Similarly, a 512x512 output will cost $0.03 ($0.03 for0.25 megapixels, rounded to 1 megapixel)" - <https://fal.ai/models/fal-ai/flux-2-pro/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "processed megapixels", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-2-pro/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-2-pro/edit>  (input schema `Flux2ProEditInput`, output schema `Flux2ProEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Flux2ProEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `image_size` | ImageSize (object) \| string | no | "auto" | ["auto", "square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. If `auto`, the size will be determined by the model. |
| `seed` | integer \| null | no | - | - | The seed to use for the generation. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enable_safety_checker` | boolean | no | true | - | Whether to enable the safety checker. Disabling it requires account authorization; unauthorized requests are always checked. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_urls` | array<string> | yes | - | - | List of URLs of input images for editing |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`Flux2ProEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |
| `seed` | integer | yes | - | - | The seed used for the generation. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** FLUX.2 [pro] instruction edit (no mask).

**Gaps:** Price in megapixels of input+output; `safety_tolerance` allowed up to '5'. Output `images` carry width/height.


### `fal-ai/gpt-image-1.5/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "GPT-Image 1.5", category `image-to-image`, tags ["openai", "gpt-image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Your request will cost different amounts based on the number of images, quality, and size. - You will be charged $0.005 per 1,000 input text tokens. One word is roughly 4 tokens. - You will be charged $0.008 per 1,000 input image tokens. One 1024x1024 image is roughly 135 tokens in low fidelity mode, or 3,050 tokens in high fidelity mode. - You will be charged $0.010 per 1,000 output text tokens. The model will consume tokens reasoning about your prompt based on it's complexity. - For low quality, you will be charged $0.009 for 1024x1024 or $0.013 for any other size *per image*. - For medium quality, you will be charged $0.034 for 1024x1024, $0.051 for 1024x1536 and $0.050 for 1536x1024 *per image*. - For high quality, you will be charged $0.133 for 1024x1024, $0.200 for 1024x1536 or $0.199 for 1536x1024 *per image*." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/fal-ai/gpt-image-1.5/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/gpt-image-1.5/edit>  (input schema `GptImage15EditInput`, output schema `GptImage15EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GptImage15EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=2, maxLength=32000] | yes | - | - | The prompt for image generation |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use as a reference for the generation. |
| `image_size` | string | no | "auto" | ["auto", "1024x1024", "1536x1024", "1024x1536"] | Aspect ratio for the generated image |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `quality` | string | no | "high" | ["low", "medium", "high"] | Quality for the generated image |
| `input_fidelity` | string | no | "high" | ["low", "high"] | Input fidelity for the generated image |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of images to generate |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `mask_image_url` | string \| null | no | - | - | The URL of the mask image to use for the generation. This indicates what part of the image to edit. |

**OUTPUT** (`GptImage15EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** OpenAI image edit with a `mask_image_url`.

**Gaps:** Mask polarity NOT STATED ('indicates what part of the image to edit'). Token-based multi-tier price.


### `openai/gpt-image-2/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "GPT Image 2 API", category `image-to-image`, tags ["gpt-image-2", "openai", "chatgpt-images-2"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text tokens (per 1M): $5.00 input, $1.25 cached, $10.00 output. Image tokens (per 1M): $8.00 input, $2.00 cached, $30.00 output. Changing the quality parameter significantly affects cost; by default we use high. Adjust it to your preference. See the description at the bottom of this page for more details on how much canonical image sizes cost. Total cost is rounded up to the closest hundredth of a cent ($0.0001)." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/openai/gpt-image-2/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=openai/gpt-image-2/edit>  (input schema `GptImage2EditInput`, output schema `GptImage2EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GptImage2EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=2, maxLength=32000] | yes | - | - | The prompt for image generation |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to use as a reference for the generation. A maximum of 16 images are allowed. |
| `image_size` | ImageSize (object) \| string | no | "auto" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto"] | The size of the generated image. Use 'auto' to infer from input images. |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `quality` | string | no | "high" | ["auto", "low", "medium", "high"] | Quality for the generated image. Use 'auto' to let the model pick the best quality for the prompt. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of images to generate |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `mask_url` | string \| null | no | - | - | The URL of the mask image to use for the generation. This indicates what part of the image to edit. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`GptImage2EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** OpenAI gpt-image-2 edit with `mask_url`, up to 16 images and `background: transparent`.

**Gaps:** Mask polarity NOT STATED. Token-based price; `quality` defaults to 'high' which the page says significantly affects cost.


### `ideogram/v4.5/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram V4.5 Edit", category `image-to-image`, tags ["realism", "typography", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.008 per image with Very Low, $0.03 with Low, $0.06 with Medium, and $0.22 with High quality. The image size does not change the price." - <https://fal.ai/models/ideogram/v4.5/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.001, `is_partner_api` = true - <https://fal.ai/models/ideogram/v4.5/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=ideogram/v4.5/edit>  (input schema `V45EditInput`, output schema `V45EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`V45EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=1, maxLength=10000] | yes | - | - | The generation or editing prompt. |
| `image_url` | string | yes | - | - | Primary source image to edit. |
| `reference_image_urls` | array<string> | no | - | - | Up to four references plus the primary source, or three references plus the primary source and mask. |
| `mask_url` | string \| null | no | - | - | Mask matching the source dimensions. Black edits, white preserves. Must contain both regions after rounding. |
| `edit_precision` | string | no | "regular" | ["regular", "high"] | regular uses Generate; high uses Precise Edit with unchanged-pixel restoration. Both always prepare structured edit instructions. |
| `quality` | string | no | "medium" | ["very_low", "low", "medium", "high"] | Quality tier for either edit precision. Defaults to medium. |
| `image_size` | ImageSize (object) \| string | no | "auto" | ["auto", "square", "square_hd", "portrait_4_3", "landscape_4_3", "portrait_16_9", "landscape_16_9"] | auto preserves source geometry, downscaled by the provider if needed. Only unmasked regular edits accept named presets or custom sizes: dimensions >=256, multiples of 32, area <=4194304, ratio <=6:1. Named presets use standard fal dimensions. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Random seed. Omit to choose automatically. |
| `sync_mode` | boolean | no | false | - | Return image data directly in the response. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`V45EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Ideogram 4.5 edit with optional mask and `edit_precision` ('high' restores unchanged pixels).

**Gaps:** Mask polarity IS stated and is OPPOSITE to the erasers: 'Black edits, white preserves.' Must contain both regions. Price table by quality tier.


### `blackforestlabs/flux-3/edit-image`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Flux 3 Image", category `image-to-image`, tags ["flux-3-image", "black-forest-labs", "multi-reference"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Image pricing varies by resolution, starting at $0.0205 per image. A 1K (1 MP) image costs $0.024. Note: these promotional launch rates are 50% off for a limited time. The discount ends October 8, after which a 1K (1 MP) image will cost $0.048" - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.024, `is_partner_api` = true - <https://fal.ai/models/blackforestlabs/flux-3/edit-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=blackforestlabs/flux-3/edit-image>  (input schema `Flux3EditImageInput`, output schema `Flux3EditImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Flux3EditImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `image_urls` | array<string> | yes | - | - | Reference image URLs or data URIs, in image 1 through image 10 order. Each must be at least 256 pixels per dimension. Oversized images are automatically downscaled to at most 4 megapixels while preserving aspect ratio. The first controls auto aspect ratio. |
| `aspect_ratio` | string | no | "auto" | ["auto", "21:9", "2:1", "16:9", "3:2", "7:5", "4:3", "5:4", "1:1", "4:5", "3:4", "5:7", "2:3", "9:16", "1:2"] | Output aspect ratio. Auto uses the first reference image when editing. |
| `resolution` | string | no | "1k" | ["512sq", "768sq", "1k", "2k", "4k"] | Output resolution tier. 4k can take several minutes. |
| `enable_prompt_expansion` | boolean | no | false | - | Expand the prompt while preserving its intent and reference roles. |
| `safety_tolerance` | integer [minimum=0, maximum=4] | no | 2 | - | Safety tolerance, from 0 (strictest) to 4. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `sync_mode` | boolean | no | false | - | Return the image as a data URI instead of storing it in request history. |
| `version` | string | no | "latest" | - | The model version. |

**OUTPUT** (`Flux3EditImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated image. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** FLUX.3 edit, up to 10 reference images.

**Gaps:** Page states a launch discount that ended October 8 (see price) - the effective price after that date is the higher one. `4k` 'can take several minutes'.


### `alibaba/qwen-image-3/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Qwen Image 3 Image Editing", category `image-to-image`, tags ["stylized", "transform", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per generated image at 1K resolution, and $0.075 per generated image at 2K resolution." - <https://fal.ai/models/alibaba/qwen-image-3/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/alibaba/qwen-image-3/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/qwen-image-3/edit>  (input schema `QwenImage3EditInput`, output schema `QwenImage3EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`QwenImage3EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=1, maxLength=5000] | yes | - | - | Text prompt describing the desired edit. Supports Chinese and English. Max 5000 characters. |
| `negative_prompt` | string [maxLength=500] \| null | no | "" | - | Content to avoid in the generated image. Max 500 characters. |
| `image_size` | ImageSize (object) \| string \| null | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. If not provided, the model determines the resolution automatically. Total number of pixels must be between 512x512 and 2048x2048. |
| `enable_prompt_expansion` | boolean | no | true | - | Enable automatic LLM prompt rewriting for better results. |
| `seed` | integer \| null | no | - | - | Random seed for reproducibility (0-2147483647). |
| `enable_safety_checker` | boolean | no | true | - | Enable content moderation for input and output. Disabling it requires account authorization; unauthorized requests are always checked. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `image_urls` | array<string> | yes | - | - | Reference images for editing (1-3 images required). Order matters: reference as 'image 1', 'image 2', 'image 3' in prompt. Resolution: 384-2048px each dimension. Max size: 10MB each. Formats: JPEG, JPG, PNG (no alpha), WEBP. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`QwenImage3EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | Generated images. |
| `seed` | integer | yes | - | - | The seed used for generation. |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Qwen-Image-3 edit, 1-3 references, prompt up to 5000 chars (Chinese/English).

**Gaps:** Input PNG must have no alpha ('PNG (no alpha)'), 384-2048 px per side, max 10MB - cut-outs with alpha need flattening first.


### `fal-ai/bytedance/seedream/v4.5/edit`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Bytedance Seedream V4.5 Edit", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/bytedance/seedream/v4.5/edit>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bytedance/seedream/v4.5/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bytedance/seedream/v4.5/edit>  (input schema `BytedanceSeedreamV45EditInput`, output schema `BytedanceSeedreamV45EditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`BytedanceSeedreamV45EditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The text prompt used to edit the image |
| `image_size` | ImageSize (object) \| string | no | {"width": 2048, "height": 2048} | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto_2K", "auto_4K"] | The size of the generated image. Width and height must be between 1920 and 4096, or total number of pixels must be between 2560*1440 and 4096*4096. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | Number of separate model generations to be run with the prompt. |
| `max_images` | integer [minimum=1, maximum=6] | no | 1 | - | If set to a number greater than one, enables multi-image generation. The model will potentially return up to `max_images` images every generation, and in total, `num_images` generations will be carried out. In total, the number of images generated will be between `num_images` and `max_images*num_images`. The total number of images (image inputs + image outputs) must not exceed 15 |
| `seed` | integer \| null | no | - | - | Random seed to control the stochasticity of image generation. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `image_urls` | array<string> | yes | - | - | List of URLs of input images for editing. Presently, up to 10 image inputs are allowed. If over 10 images are sent, only the last 10 will be used. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`BytedanceSeedreamV45EditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated images |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Seedream 4.5 instruction edit, up to 10 input images.

**Gaps:** `image_size` options start at 2K ('Width and height must be between 1920 and 4096, or total number of pixels must be between 2560*1440 and 4096*4096') - small thumbnails will be produced at a much larger size than the 1280x720 target. `is_partner_api` is true.


## 7. Text-to-image (thumbnail backgrounds and elements)


### `fal-ai/recraft/v3/text-to-image`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Recraft V3", category `text-to-image`, tags ["vector", "typography", "style"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image (or $0.08 if you are using a vector style). For $1 you can run this model approximately 25 times." - <https://fal.ai/models/fal-ai/recraft/v3/text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/fal-ai/recraft/v3/text-to-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/recraft/v3/text-to-image>  (input schema `RecraftV3TextToImageInput`, output schema `RecraftV3TextToImageOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`RecraftV3TextToImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=1, maxLength=1000] | yes | - | - | - |
| `image_size` | ImageSize (object) \| string | no | "square_hd" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | - |
| `style` | string | no | "realistic_image" | ["any", "realistic_image", "digital_illustration", "vector_illustration", "realistic_image/b_and_w", "realistic_image/hard_flash", "realistic_image/hdr", "realistic_image/natural_light", "realistic_image/studio_portrait", "realistic_image/enterprise", "realistic_image/motion_blur", "realistic_image/evening_light", "realistic_image/faded_nostalgia", "realistic_image/forest_life", "realistic_image/mystic_naturalism", "realistic_image/natural_tones", "realistic_image/organic_calm", "realistic_image/real_life_glow", "realistic_image/retro_realism", "realistic_image/retro_snapshot", "realistic_image/urban_drama", "realistic_image/village_realism", "realistic_image/warm_folk", "digital_illustration/pixel_art", "digital_illustration/hand_drawn", "digital_illustration/grain", "digital_illustration/infantile_sketch", "digital_illustration/2d_art_poster", "digital_illustration/handmade_3d", "digital_illustration/hand_drawn_outline", "digital_illustration/engraving_color", "digital_illustration/2d_art_poster_2", "digital_illustration/antiquarian", "digital_illustration/bold_fantasy", "digital_illustration/child_book", "digital_illustration/child_books", "digital_illustration/cover", "digital_illustration/crosshatch", "digital_illustration/digital_engraving", "digital_illustration/expressionism", "digital_illustration/freehand_details", "digital_illustration/grain_20", "digital_illustration/graphic_intensity", "digital_illustration/hard_comics", "digital_illustration/long_shadow", "digital_illustration/modern_folk", "digital_illustration/multicolor", "digital_illustration/neon_calm", "digital_illustration/noir", "digital_illustration/nostalgic_pastel", "digital_illustration/outline_details", "digital_illustration/pastel_gradient", "digital_illustration/pastel_sketch", "digital_illustration/pop_art", "digital_illustration/pop_renaissance", "digital_illustration/street_art", "digital_illustration/tablet_sketch", "digital_illustration/urban_glow", "digital_illustration/urban_sketching", "digital_illustration/vanilla_dreams", "digital_illustration/young_adult_book", "digital_illustration/young_adult_book_2", "vector_illustration/bold_stroke", "vector_illustration/chemistry", "vector_illustration/colored_stencil", "vector_illustration/contour_pop_art", "vector_illustration/cosmics", "vector_illustration/cutout", "vector_illustration/depressive", "vector_illustration/editorial", "vector_illustration/emotional_flat", "vector_illustration/infographical", "vector_illustration/marker_outline", "vector_illustration/mosaic", "vector_illustration/naivector", "vector_illustration/roundish_flat", "vector_illustration/segmented_colors", "vector_illustration/sharp_contrast", "vector_illustration/thin", "vector_illustration/vector_photo", "vector_illustration/vivid_shapes", "vector_illustration/engraving", "vector_illustration/line_art", "vector_illustration/line_circuit", "vector_illustration/linocut"] | The style of the generated images. Vector images cost 2X as much. |
| `colors` | array<RGBColor (object)> | no | [] | - | An array of preferable colors |
| `style_id` | string [format=uuid4] \| null | no | - | - | The ID of the custom style reference (optional) |
| `enable_safety_checker` | boolean | no | false | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

*Nested `RGBColor` (at `colors`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `colors.r` | integer [minimum=0, maximum=255] | no | 0 | - | Red color value |
| `colors.g` | integer [minimum=0, maximum=255] | no | 0 | - | Green color value |
| `colors.b` | integer [minimum=0, maximum=255] | no | 0 | - | Blue color value |

**OUTPUT** (`RecraftV3TextToImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Design-style generation (vector/illustration/photo) with colour hints (`colors`) - for graphic backgrounds and icons.

**Gaps:** `prompt` capped at 1000 chars. Vector styles cost double (stated in `style` description). Output `File` has no width/height. `enable_safety_checker` defaults to false.


### `fal-ai/ideogram/v3`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Ideogram Text to Image", category `text-to-image`, tags ["realism", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with QUALITY." - <https://fal.ai/models/fal-ai/ideogram/v3>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3>  (input schema `IdeogramV3Input`, output schema `IdeogramV3Output`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`IdeogramV3Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_urls` | array<string> \| null | no | - | - | A set of images to use as style references (maximum total size 10MB across all style references). The images should be in JPEG, PNG or WebP format |
| `rendering_speed` | string | no | "BALANCED" | ["TURBO", "BALANCED", "QUALITY"] | The rendering speed to use. |
| `color_palette` | ColorPalette (object) \| null | no | - | - | A color palette for generation, must EITHER be specified via one of the presets (name) or explicitly via hexadecimal representations of the color with optional weights (members) |
| `style_codes` | array<string> \| null | no | - | - | A list of 8 character hexadecimal codes representing the style of the image. Cannot be used in conjunction with style_reference_images or style |
| `style` | string \| null | no | - | ["AUTO", "GENERAL", "REALISTIC", "DESIGN"] | The style type to generate with. Cannot be used with style_codes. |
| `expand_prompt` | boolean | no | true | - | Determine if MagicPrompt should be used in generating the request or not. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `style_preset` | string \| null | no | - | (same 62-value list as `fal-ai/ideogram/v3/edit -> style_preset`) | Style preset for generation. The chosen style preset will guide the generation. |
| `prompt` | string | yes | - | - | - |
| `image_size` | ImageSize (object) \| string \| null | no | "square_hd" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The resolution of the generated image |
| `negative_prompt` | string | no | "" | - | Description of what to exclude from an image. Descriptions in the prompt take precedence to descriptions in the negative prompt. |

*Nested `ColorPalette` (at `color_palette`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members` | array<ColorPaletteMember (object)> \| null | no | - | - | A list of color palette members that define the color palette |
| `color_palette.name` | string \| null | no | - | ["EMBER", "FRESH", "JUNGLE", "MAGIC", "MELON", "MOSAIC", "PASTEL", "ULTRAMARINE"] | A color palette preset value |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

*Nested `ColorPaletteMember` (at `color_palette.members`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb` | RGBColor (object) | yes | - | - | RGB color value for the palette member |
| `color_palette.members.color_weight` | number [minimum=0.05, maximum=1] \| null | no | 0.5 | - | The weight of the color in the color palette |

*Nested `RGBColor` (at `color_palette.members.rgb`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `color_palette.members.rgb.r` | integer [minimum=0, maximum=255] | no | 0 | - | Red color value |
| `color_palette.members.rgb.g` | integer [minimum=0, maximum=255] | no | 0 | - | Green color value |
| `color_palette.members.rgb.b` | integer [minimum=0, maximum=255] | no | 0 | - | Blue color value |

**OUTPUT** (`IdeogramV3Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Text-to-image where rendering readable words matters (gallery tags: typography).

**Gaps:** Price by rendering speed ($0.03/0.06/0.09). Output `File` has no width/height; with `image_size` as object, bounds are width/height < 14142.


### `fal-ai/flux-pro/v1.1-ultra`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX1.1 [pro] ultra", category `text-to-image`, tags ["high-res", "realism"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.06 per image. Images are billed by rounding up to the nearest megapixel." - <https://fal.ai/models/fal-ai/flux-pro/v1.1-ultra>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.06, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-pro/v1.1-ultra>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-pro/v1.1-ultra>  (input schema `FluxProV11UltraInput`, output schema `FluxProV11UltraOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`FluxProV11UltraInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enhance_prompt` | boolean | no | false | - | Whether to enhance the prompt for better results. |
| `image_url` | string \| null | no | - | - | The image URL to generate an image from. |
| `image_prompt_strength` | number [minimum=0, maximum=1] | no | 0.1 | - | The strength of the image prompt, between 0 and 1. |
| `aspect_ratio` | string \| string | no | "16:9" | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `raw` | boolean | no | false | - | Generate less processed, more natural-looking images. |

**OUTPUT** (`FluxProV11UltraOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<registry__image__fast_sdxl__models__Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `registry__image__fast_sdxl__models__Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** High-resolution (up to 2K) text-to-image, optional image prompt.

**Gaps:** Price statement is per image, but the same sentence says images are 'billed by rounding up to the nearest megapixel' - ambiguous. `aspect_ratio` is the only size control.


### `fal-ai/nano-banana`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Nano Banana", category `text-to-image`, tags ["image-generation"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.039 per image. For $1.00, you can run this model 25 times." - <https://fal.ai/models/fal-ai/nano-banana>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.0398, `is_partner_api` = true - <https://fal.ai/models/fal-ai/nano-banana>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/nano-banana>  (input schema `NanoBananaInput`, output schema `NanoBananaOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`NanoBananaInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The text prompt to generate an image from. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string | no | "1:1" | ["21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16"] | The aspect ratio of the generated image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate. |

**OUTPUT** (`NanoBananaOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Fast general text-to-image with Gemini image model; cheap.

**Gaps:** No resolution parameter; aspect ratio only.


### `fal-ai/flux/dev`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "FLUX.1 [dev]", category `text-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.025 per megapixel. Images are billed by rounding up to the nearest megapixel." - <https://fal.ai/models/fal-ai/flux/dev>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.025, `is_partner_api` = false - <https://fal.ai/models/fal-ai/flux/dev>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux/dev>  (input schema `FluxDevInput`, output schema `FluxDevOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`FluxDevInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `image_size` | ImageSize (object) \| string | no | "landscape_4_3" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 28 | - | The number of inference steps to perform. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `guidance_scale` | number [minimum=1, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked, and images flagged as unsafe are returned as black images. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `acceleration` | string | no | "none" | ["none", "regular", "high"] | The speed of the generation. The higher the speed, the faster the generation. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`FluxDevOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated image files info. |
| `timings` | object | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |

**Use in the editor:** FLUX dev text-to-image; priced per megapixel.

**Gaps:** Text-rendering quality is not stated in the schema. Price is per megapixel, 'rounded up to the nearest megapixel'.


### `fal-ai/bytedance/seedream/v4/text-to-image`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Bytedance Seedream V4 Text To Image", category `text-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per image." - <https://fal.ai/models/fal-ai/bytedance/seedream/v4/text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/bytedance/seedream/v4/text-to-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/bytedance/seedream/v4/text-to-image>  (input schema `BytedanceSeedreamV4TextToImageInput`, output schema `BytedanceSeedreamV4TextToImageOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`BytedanceSeedreamV4TextToImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The text prompt used to generate the image |
| `image_size` | ImageSize (object) \| string | no | {"width": 2048, "height": 2048} | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto", "auto_2K", "auto_4K"] | The size of the generated image. Total pixels must be between 960x960 and 4096x4096. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | Number of separate model generations to be run with the prompt. |
| `max_images` | integer [minimum=1, maximum=6] | no | 1 | - | If set to a number greater than one, enables multi-image generation. The model will potentially return up to `max_images` images every generation, and in total, `num_images` generations will be carried out. In total, the number of images generated will be between `num_images` and `max_images*num_images`. |
| `seed` | integer \| null | no | - | - | Random seed to control the stochasticity of image generation. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `enhance_prompt_mode` | string | no | "standard" | ["standard", "fast"] | The mode to use for enhancing prompt enhancement. Standard mode provides higher quality results but takes longer to generate. Fast mode provides average quality results but takes less time to generate. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`BytedanceSeedreamV4TextToImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated images |
| `seed` | integer | yes | - | - | Seed used for generation |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Text-to-image up to 4K sizes.

**Gaps:** Total pixels must be between 960x960 and 4096x4096.


### `fal-ai/flux-2/pro`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404; model page is the not-found shell. The gallery lists the endpoint as `fal-ai/flux-2-pro` (no slash between `flux-2` and `pro`), documented below as a discovered sibling - note it is TEXT-to-image only in its schema although the gallery blurb says 'Image editing'; the editing variant is `fal-ai/flux-2-pro/edit` (section 6).

### `fal-ai/flux-2-pro`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Flux 2 Pro", category `text-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Your request will cost $0.03 for the first megapixel of output, plus $0.015 per extra megapixel of input and output, rounded up to the nearest megapixel. For example, a 1024x1024 image will cost $0.03, and a 1920x1080 image will cost $0.045 ($0.03 for first megapixel + $0.015 for the second megapixel). Similarly, a 512x512 output will cost $0.03 ($0.03 for 0.25 megapixels, rounded to 1 megapixel)" - <https://fal.ai/api/models?categories=text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "processed megapixels", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/flux-2-pro>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/flux-2-pro>  (input schema `Flux2ProInput`, output schema `Flux2ProOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`Flux2ProInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `image_size` | ImageSize (object) \| string | no | "landscape_4_3" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. |
| `seed` | integer \| null | no | - | - | The seed to use for the generation. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5"] | The safety tolerance level for the generated image. 1 being the most strict and 5 being the most permissive. |
| `enable_safety_checker` | boolean | no | true | - | Whether to enable the safety checker. Disabling it requires account authorization; unauthorized requests are always checked. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`Flux2ProOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |
| `seed` | integer | yes | - | - | The seed used for the generation. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** FLUX.2 [pro] TEXT-to-image (the schema has no image input). This is the id the request called `fal-ai/flux-2/pro`.

**Gaps:** Gallery blurb says 'Image editing with FLUX.2 [pro] ...' but this endpoint's input has no `image_urls`; editing is `fal-ai/flux-2-pro/edit`. `is_partner_api` is true.


### `fal-ai/nano-banana-pro`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Nano Banana Pro", category `text-to-image`, tags ["realism", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.15 per image. For $1.00, you can run this model 7 times. 4K outputs will be charged at double the standard rate. If web search is used, an additional $0.015 will be charged. Note: Pricing may change in the future." - <https://fal.ai/models/fal-ai/nano-banana-pro>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.15, `is_partner_api` = true - <https://fal.ai/models/fal-ai/nano-banana-pro>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/nano-banana-pro>  (input schema `NanoBananaProInput`, output schema `NanoBananaProOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`NanoBananaProInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The text prompt to generate an image from. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string \| null | no | "1:1" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16"] | The aspect ratio of the generated image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `system_prompt` | string [maxLength=50000] | no | "" | - | Optional system instruction that steers the model's persona and output style across the request. Leave blank to omit; when provided, it is sent as the system instruction to Gemini (or as a system message on OpenAI-compatible providers). |
| `resolution` | string | no | "1K" | ["1K", "2K", "4K"] | The resolution of the image to generate. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate. |
| `enable_web_search` | boolean | no | false | - | Enable web search for the image generation task. This will allow the model to use the latest information from the web to generate the image. |

**OUTPUT** (`NanoBananaProOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |
| `description` | string | yes | - | - | The description of the generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Higher-quality nano-banana text-to-image; 1K/2K/4K.

**Gaps:** 4K costs double (price sentence).


### `openai/gpt-image-2`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "GPT Image 2 API", category `text-to-image`, tags ["gpt-image-2", "openai", "typography", "chatgpt-images-2"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text tokens (per 1M): $5.00 input, $1.25 cached, $10.00 output. Image tokens (per 1M): $8.00 input, $2.00 cached, $30.00 output. Changing the quality parameter significantly affects cost; by default we use high. Adjust it to your preference. See the description at the bottom of this page for more details on how much canonical image sizes cost. Total cost is rounded up to the closest hundredth of a cent ($0.0001.)" - <https://fal.ai/api/models?categories=text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/openai/gpt-image-2>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=openai/gpt-image-2>  (input schema `GptImage2Input`, output schema `GptImage2Output`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`GptImage2Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=2, maxLength=32000] | yes | - | - | The prompt for image generation |
| `image_size` | ImageSize (object) \| string | no | "landscape_4_3" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto"] | The size of the generated image. Supports preset names, explicit {width, height}, or 'auto' to let the model pick the best size. Concrete sizes must have both dimensions as multiples of 16, max edge 3840px, aspect ratio <= 3:1, total pixels between 655,360 and 8,294,400. |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `quality` | string | no | "high" | ["auto", "low", "medium", "high"] | Quality for the generated image. Use 'auto' to let the model pick the best quality for the prompt. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | Number of images to generate |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`GptImage2Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** OpenAI gpt-image-2 text-to-image; `background: transparent` option.

**Gaps:** Token-based price; concrete sizes must satisfy multiples of 16, max edge 3840, aspect <= 3:1.


### `ideogram/v4.5`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram V4.5 Text to Image", category `text-to-image`, tags ["realism", "typography", "stylized"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 per image with Low, $0.06 with Medium, and $0.22 with High quality. The image size does not change the price." - <https://fal.ai/models/ideogram/v4.5>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.001, `is_partner_api` = true - <https://fal.ai/models/ideogram/v4.5>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=ideogram/v4.5>  (input schema `V45Input`, output schema `V45Output`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`V45Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string [minLength=1, maxLength=10000] | yes | - | - | The generation or editing prompt. |
| `image_size` | ImageSize (object) \| string | no | "square_hd" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | Output size. square is promoted to square_hd (1024x1024). 4:3 presets use 1152x864 or 864x1152; 16:9 presets use 1280x720 or 720x1280. Explicit dimensions must match a supported size: 1024x1024, 1024x3072, 1120x896, 1152x2944, 1152x864, 1248x3328, 1248x832, 1280x3072, 1280x720, 1280x800, 1296x3168, 1440x2560, 1440x2880, 1440x720, 1600x2560, 1664x2496, 1728x2304, 1792x2240, 2048x2048, 2240x1792, 2304x1728, 2496x1664, 2560x1440, 2560x1600, 2880x1440, 2944x1152, 3072x1024, 3072x1280, 3168x1296, 3328x1248, 720x1280, 720x1440, 800x1280, 832x1248, 864x1152, 896x1120 |
| `quality` | string | no | "medium" | ["low", "medium", "high"] | Text-to-image quality. |
| `enable_prompt_expansion` | boolean | no | true | - | Enable partner prompt expansion. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Random seed. Omit to choose automatically. |
| `sync_mode` | boolean | no | false | - | Return image data directly in the response. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`V45Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Ideogram 4.5 text-to-image with quality tiers.

**Gaps:** Explicit `image_size` must match one of the sizes listed in the `image_size` description (see INPUT table).


### `blackforestlabs/flux-3/text-to-image`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Flux 3 Image", category `text-to-image`, tags ["flux-3-image", "black-forest-labs", "typography", "4k"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Image pricing varies by resolution, starting at $0.0205 per image. A 1K (1 MP) image costs $0.024. Note: these promotional launch rates are 50% off for a limited time. The discount ends October 8, after which a 1K (1 MP) image will cost $0.048" - <https://fal.ai/api/models?categories=text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.024, `is_partner_api` = true - <https://fal.ai/models/blackforestlabs/flux-3/text-to-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=blackforestlabs/flux-3/text-to-image>  (input schema `Flux3TextToImageInput`, output schema `Flux3TextToImageOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`Flux3TextToImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | The prompt to generate an image from. |
| `aspect_ratio` | string | no | "auto" | (same 15-value list as `blackforestlabs/flux-3/edit-image -> aspect_ratio`) | Output aspect ratio. Auto uses the first reference image when editing. |
| `resolution` | string | no | "1k" | ["512sq", "768sq", "1k", "2k", "4k"] | Output resolution tier. 4k can take several minutes. |
| `enable_prompt_expansion` | boolean | no | false | - | Expand the prompt while preserving its intent and reference roles. |
| `safety_tolerance` | integer [minimum=0, maximum=4] | no | 2 | - | Safety tolerance, from 0 (strictest) to 4. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `sync_mode` | boolean | no | false | - | Return the image as a data URI instead of storing it in request history. |
| `version` | string | no | "latest" | - | The model version. |

**OUTPUT** (`Flux3TextToImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated image. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** FLUX.3 text-to-image up to 4k.

**Gaps:** Promotional price ended October 8 per the page text (see price).


### `fal-ai/ideogram/v3/generate-transparent`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram Transparent", category `text-to-image`, tags ["stylized", "transform", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.03 with TURBO, $0.06 with BALANCED, and $0.09 with QUALITY." - <https://fal.ai/models/fal-ai/ideogram/v3/generate-transparent>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.03, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3/generate-transparent>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3/generate-transparent>  (input schema `IdeogramV3GenerateTransparentInput`, output schema `IdeogramV3GenerateTransparentOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`IdeogramV3GenerateTransparentInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt` | string | yes | - | - | - |
| `aspect_ratio` | string | no | "1:1" | ["1:3", "3:1", "1:2", "2:1", "9:16", "16:9", "10:16", "16:10", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "1:1"] | The aspect ratio of the generated image. |
| `rendering_speed` | string | no | "BALANCED" | ["TURBO", "BALANCED", "QUALITY"] | The rendering speed to use. |
| `expand_prompt` | boolean | no | true | - | Determine if MagicPrompt should be used in generating the request or not. |
| `negative_prompt` | string | no | "" | - | Description of what to exclude from an image. Descriptions in the prompt take precedence to descriptions in the negative prompt. |
| `num_images` | integer [minimum=1, maximum=8] | no | 1 | - | Number of images to generate. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`IdeogramV3GenerateTransparentOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<File (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | Seed used for the random number generator |

*Nested `File` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Generates an element with a transparent background directly - sticker/icon layers without a cut-out step.

**Gaps:** Aspect ratios only (15 values); no explicit pixel size. Whether output is PNG with alpha is not stated in the schema (description-less `File`).


## 8. Style, colour and miscellaneous


### `fal-ai/image-editing/style-transfer`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Style Transfer", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/style-transfer>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/style-transfer>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/style-transfer>  (input schema `ImageEditingStyleTransferInput`, output schema `ImageEditingStyleTransferOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingStyleTransferInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string | no | "Van Gogh's Starry Night" | - | The artistic style to apply. |

**OUTPUT** (`ImageEditingStyleTransferOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Apply a named style via `prompt` (default 'Van Gogh's Starry Night').

**Gaps:** `image_url` description is copy-pasted ('Image prompt for the omni model.').


### `fal-ai/image-editing/color-correction`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Color Correction", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/color-correction>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/color-correction>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/color-correction>  (input schema `ImageEditingColorCorrectionInput`, output schema `ImageEditingColorCorrectionOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingColorCorrectionInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingColorCorrectionOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** One-click colour correction with no parameters beyond sampling.

**Gaps:** No strength/intent controls; result is generative and may shift content.


### `fal-ai/image-editing/cartoonify`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Cartoonify", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/cartoonify>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/cartoonify>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/cartoonify>  (input schema `ImageEditingCartoonifyInput`, output schema `ImageEditingCartoonifyOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingCartoonifyInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Image prompt for the omni model. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingCartoonifyOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Cartoonify with no prompt.

**Gaps:** As above.


### `fal-ai/image-editing/sketch`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404; model page is the not-found shell; not in the gallery (gallery has `bria/fibo-edit/sketch_to_colored_image`, not fetched).

### `fal-ai/image-editing/retouch`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Retouch", category `image-to-image`, tags [], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/retouch>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/retouch>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/retouch>  (input schema `ImageEditingRetouchInput`, output schema `ImageEditingRetouchOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingRetouchInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to retouch. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `enable_safety_checker` | boolean | no | true | - | Whether to enable the safety checker for the generated image. |
| `lora_scale` | number [minimum=0, maximum=4] | no | 1 | - | The scale factor for the LoRA model. Controls the strength of the LoRA effect. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingRetouchOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Face/skin retouch (LoRA, `lora_scale`).

**Gaps:** Not stated what is retouched beyond 'URL of the image to retouch.'


### `fal-ai/image-editing/photo-restoration`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: title "Image Editing Photo Restoration", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/photo-restoration>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/photo-restoration>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/photo-restoration>  (input schema `ImageEditingPhotoRestorationInput`, output schema `ImageEditingPhotoRestorationOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingPhotoRestorationInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the old or damaged photo to restore. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `safety_tolerance` | string | no | "2" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for the generated image. 1 being the most strict and 6 being the most permissive. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The format of the generated image. |
| `aspect_ratio` | string \| null | no | - | ["21:9", "16:9", "4:3", "3:2", "1:1", "2:3", "3:4", "9:16", "9:21"] | The aspect ratio of the generated image. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`ImageEditingPhotoRestorationOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Old/damaged photo restoration.

**Gaps:** Not thumbnail-specific.


### `fal-ai/invisible-watermark`

- **Status: NOT ESTABLISHED** (requested candidate).
- What was tried: schema URL returns HTTP 404 (also on a retry) although the gallery lists the id (title 'Invisible Watermark', category `image-to-image`, shortDescription 'Invisible Watermark is a model that can add an invisible watermark to an image.', no price override); model page has no billing metadata. Inputs/outputs therefore cannot be established.

### `fal-ai/imageutils/nsfw`

- **Status: established** (requested candidate); schema HTTP 200.
- Gallery entry: not present in the image-to-image / text-to-image listings.
- Price:
  - Page statement (verbatim): "Your request will cost $0.001 per image." - <https://fal.ai/models/fal-ai/imageutils/nsfw>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.001, `is_partner_api` = false - <https://fal.ai/models/fal-ai/imageutils/nsfw>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/imageutils/nsfw>  (input schema `ImageutilsNsfwInput`, output schema `ImageutilsNsfwOutput`)
- Category (x-fal-metadata): `vision`

**INPUT** (`ImageutilsNsfwInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | Input image url. |

**OUTPUT** (`ImageutilsNsfwOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `nsfw_probability` | number | yes | - | - | The probability of the image being NSFW. |

**Use in the editor:** Safety gate: returns a probability for an image; the cheapest endpoint found.

**Gaps:** Category in metadata is `vision`. The threshold meaning of `nsfw_probability` is not documented.


### `fal-ai/image-editing/youtube-thumbnails`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Image Editing Youtube Thumbnails", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.04 per image." - <https://fal.ai/models/fal-ai/image-editing/youtube-thumbnails>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = false - <https://fal.ai/models/fal-ai/image-editing/youtube-thumbnails>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/image-editing/youtube-thumbnails>  (input schema `ImageEditingYoutubeThumbnailsInput`, output schema `ImageEditingYoutubeThumbnailsOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`ImageEditingYoutubeThumbnailsInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the image to convert to YouTube thumbnail style. |
| `guidance_scale` | number [minimum=0, maximum=20] | no | 3.5 | - | The CFG (Classifier Free Guidance) scale is a measure of how close you want the model to stick to your prompt when looking for a related image to show you. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 30 | - | Number of inference steps for sampling. |
| `enable_safety_checker` | boolean | no | true | - | Whether to enable the safety checker for the generated image. |
| `lora_scale` | number [minimum=0, maximum=4] | no | 0.5 | - | The scale factor for the LoRA model. Controls the strength of the LoRA effect. |
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string | no | "Generate youtube thumbnails" | - | The text to include in the YouTube thumbnail. |

**OUTPUT** (`ImageEditingYoutubeThumbnailsOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | - |
| `seed` | integer | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |

**Use in the editor:** Converts an image to YouTube-thumbnail style via LoRA; `prompt` is the text to put in the thumbnail.

**Gaps:** Schema gives no description of the visual treatment; text rendering quality must be measured. `lora_scale` default 0.5.


### `fal-ai/ideogram/v3/layerize-text`

- **Status: established** (discovered via the gallery listing (sibling not in the requested list)); schema HTTP 200.
- Gallery entry: title "Ideogram", category `image-to-image`, tags ["stylized", "transform", "typography"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.09 per image." - <https://fal.ai/models/fal-ai/ideogram/v3/layerize-text>
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.09, `is_partner_api` = true - <https://fal.ai/models/fal-ai/ideogram/v3/layerize-text>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/ideogram/v3/layerize-text>  (input schema `IdeogramV3LayerizeTextInput`, output schema `IdeogramV3LayerizeTextOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`IdeogramV3LayerizeTextInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | URL of the flat graphic image to layerize. Must be JPEG, PNG, or WebP (max 10MB). |
| `prompt` | string \| null | no | - | - | Optional prompt to guide the layerization. |
| `seed` | integer \| null | no | - | - | Seed for the random number generator. |
| `font_file_h1_url` | string \| null | no | - | - | URL of the font file to use for h1 text. Cannot be used with font_name_h1. |
| `font_name_h1` | string \| null | no | - | - | Name of the font to use for h1 text. Cannot be used with font_file_h1_url. |
| `font_file_h2_url` | string \| null | no | - | - | URL of the font file to use for h2 text. Cannot be used with font_name_h2. |
| `font_name_h2` | string \| null | no | - | - | Name of the font to use for h2 text. Cannot be used with font_file_h2_url. |
| `font_file_body_url` | string \| null | no | - | - | URL of the font file to use for body text. Cannot be used with font_name_body. |
| `font_name_body` | string \| null | no | - | - | Name of the font to use for body text. Cannot be used with font_file_body_url. |
| `font_file_small_url` | string \| null | no | - | - | URL of the font file to use for small text. Cannot be used with font_name_small. |
| `font_name_small` | string \| null | no | - | - | Name of the font to use for small text. Cannot be used with font_file_small_url. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |

**OUTPUT** (`IdeogramV3LayerizeTextOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | The background image with text removed. |
| `prompt` | string | yes | - | - | The prompt used for layerization. May differ from the input prompt if MagicPrompt expanded it. |
| `text_containers` | array<object> \| null | no | - | - | Structured text containers with hierarchical text data (container -> items -> spans). |
| `text_html` | string \| null | no | - | - | Rendered overlay HTML for compositing text over the background image. |
| `image_layers` | array<object> | no | - | - | Image layers extracted from the design. |
| `seed` | integer | yes | - | - | Seed used for the random number generator. |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Splits a flat graphic into a text-free background plus text containers and HTML, with optional fonts - the closest thing found to 'make existing thumbnail text editable'.

**Gaps:** `text_containers`, `text_html`, `image_layers` are untyped `object` arrays in the schema - the exact shape (positions, font sizes, colours) is NOT STATED and must be measured. Fonts via `font_name_*` (valid names not enumerated) or `font_file_*_url`. Max 10MB input.


## 9. Added by the owner directive of 2026-10-10 (newest model families)

The owner asked that the editor use the NEWEST variant of each model family instead of the older ids of the v1 table above. Each id below was probed on 2026-10-10 (`https://fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}` answering HTTP 200), its schema transcribed mechanically from that document, and its price taken from `https://fal.ai/models/{id}` (the rendered sentence, its `llms.txt` copy, the embedded billing metadata and, where the id is in the gallery, `pricingInfoOverride`). Ids that this directive added: `fal-ai/sam-3-1/image`, `google/nano-banana-2.1`, `openai/gpt-image-2.5/sunburst/edit`, `openai/gpt-image-2.5/flare/edit`, `bytedance/seedream/v5/pro/edit`, `bytedance/seedream/v5/pro/text-to-image`, `fal-ai/qwen-image-edit-2511`, `topaz/upscale/image/generative`, `bria/fibo-edit/relight`, `bria/fibo-edit/edit`, `bria/fibo-edit/erase_by_text`. Already documented above and now promoted to defaults or alternatives: `google/nano-banana-2.1/edit`, `ideogram/v4.5`, `ideogram/v4.5/edit`, `blackforestlabs/flux-3/edit-image`, `blackforestlabs/flux-3/text-to-image`, `fal-ai/seedvr/upscale/image`, `topaz/upscale/image/precision`, `fal-ai/flux-2-pro/outpaint`, `fal-ai/bria/background/replace`, `fal-ai/image-apps-v2/relighting`, `fal-ai/object-removal/mask`. Established NOT to exist (HTTP 404): `fal-ai/seedvr2/upscale/image`, `fal-ai/flux-3-pro/outpaint`, `ideogram/v4.5/generate-transparent`.

### `fal-ai/sam-3-1/image`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Sam 3 1", category `image-to-image`, tags ["segmentation", "mask", "real-time"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost $0.01 per request." - <https://fal.ai/models/fal-ai/sam-3-1/image>
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Your request will cost $0.01 per request." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = false - <https://fal.ai/models/fal-ai/sam-3-1/image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/sam-3-1/image>  (input schema `Sam31ImageInput`, output schema `Sam31ImageOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`Sam31ImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `include_boxes` | boolean | no | false | - | Whether to include bounding boxes for each mask (when available). |
| `point_prompts` | array<PointPrompt (object)> | no | [] | - | List of point prompts |
| `include_scores` | boolean | no | false | - | Whether to include mask confidence scores. |
| `image_url` | string | yes | - | - | URL of the image to be segmented |
| `max_masks` | integer [minimum=1, maximum=32] | no | 3 | - | Maximum number of masks to return when `return_multiple_masks` is enabled. |
| `apply_mask` | boolean | no | true | - | Apply the mask on the image. |
| `prompt` | string | no | "wheel" | - | Text prompt for segmentation |
| `return_multiple_masks` | boolean | no | false | - | If True, upload and return multiple generated masks as defined by `max_masks`. |
| `box_prompts` | array<BoxPrompt (object)> | no | [] | - | Box prompt coordinates (x_min, y_min, x_max, y_max). Multiple boxes supported - use object_id to group boxes for the same object or leave empty for separate objects. |
| `sync_mode` | boolean | no | false | - | If True, the media will be returned as a data URI. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |

*Nested `PointPrompt` (at `point_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `point_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Prompts sharing an object id refine the same object. When a text prompt is also given, the id selects which detected object the points refine. |
| `point_prompts.y` | integer \| null | no | - | - | Y Coordinate of the prompt |
| `point_prompts.label` | integer \| null | no | - | [0, 1] | 1 for foreground, 0 for background |
| `point_prompts.x` | integer \| null | no | - | - | X Coordinate of the prompt |
| `point_prompts.frame_index` | integer \| null | no | - | - | The frame index to interact with. |

*Nested `BoxPrompt` (at `box_prompts`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `box_prompts.y_max` | integer \| null | no | - | - | Y Max Coordinate of the box |
| `box_prompts.object_id` | integer \| null | no | - | - | Optional object identifier. Boxes sharing an object id refine the same object. |
| `box_prompts.x_max` | integer \| null | no | - | - | X Max Coordinate of the box |
| `box_prompts.y_min` | integer \| null | no | - | - | Y Min Coordinate of the box |
| `box_prompts.x_min` | integer \| null | no | - | - | X Min Coordinate of the box |
| `box_prompts.frame_index` | integer \| null | no | - | - | The frame index to interact with. |

**OUTPUT** (`Sam31ImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `metadata` | array<MaskMetadata (object)> \| null | no | - | - | Per-mask metadata including scores and boxes. |
| `scores` | array<number \| null> \| null | no | - | - | Per-mask confidence scores when requested. |
| `image` | Image (object) \| null | no | - | - | Primary segmented mask preview. |
| `masks` | array<Image (object)> | yes | - | - | Segmented mask images. |
| `boxes` | array<array<number> \| null> \| null | no | - | - | Per-mask normalized bounding boxes [cx, cy, w, h] when requested. |

*Nested `Image` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `image.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |

*Nested `Image` (at `masks`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `masks.content_type` | string \| null | no | - | - | The mime type of the file. |
| `masks.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `masks.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `masks.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `masks.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `masks.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |

**Use in the editor:** The default segmenter. Text, click and box prompts, several candidate masks (`return_multiple_masks`, `max_masks` 1-32), per-mask scores and boxes. Billed per REQUEST, not per mask.

**Gaps:** The schema declares point/box coordinates as integers and says nothing about pixels versus a normalized grid; `image` is only 'Primary segmented mask preview' (whether `apply_mask` returns an RGBA cut-out or a visualisation is not stated); mask polarity of `masks[]` is not stated; the default `prompt` is 'wheel' (never sent unless the caller supplied a prompt) and `text_prompt` is deprecated; output `boxes`/`metadata.box` are normalized [cx, cy, w, h] while input boxes are x_min/y_min/x_max/y_max. Identical schema to `fal-ai/sam-3/image` (section 1), which is $0.005 per request.


### `google/nano-banana-2.1`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Nano Banana 2.1", category `text-to-image`, tags ["image generation", "photorealism", "typography", "text rendering"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text/thinking tokens (per 1M): **$1.764** input, **$8.823** output. Image tokens (per 1M): **$1.764** input, **$35.294** output. Changing the **resolution** parameter significantly affects cost. See the description at the bottom of this page for more details on how much canonical image sizes cost." - <https://fal.ai/api/models?categories=text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/google/nano-banana-2.1>
  - Canonical-size price table on the model page (verbatim):

    | Output resolution | Text-to-image | Edit with two 1K input images |
    |---|---|---|
    | 1K | $0.040 | $0.044 |
    | 2K | $0.059 | $0.063 |
    | 4K | $0.134 | $0.138 |

- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=google/nano-banana-2.1>  (input schema `NanoBanana21Input`, output schema `NanoBanana21Output`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`NanoBanana21Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `seed` | integer \| null | no | - | - | The seed for the random number generator. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `safety_tolerance` | string | no | "4" | ["1", "2", "3", "4", "5", "6"] | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `resolution` | string | no | "1K" | ["1K", "2K", "4K"] | The resolution of the image to generate. |
| `aspect_ratio` | string \| null | no | "auto" | ["auto", "21:9", "16:9", "3:2", "4:3", "5:4", "1:1", "4:5", "3:4", "2:3", "9:16", "4:1", "1:4", "8:1", "1:8"] | The aspect ratio of the generated image. Supports extreme ratios: 4:1, 1:4, 8:1, 1:8. Use "auto" to let the model decide based on the prompt. |
| `prompt` | string [minLength=3, maxLength=50000] | yes | - | - | The text prompt to generate an image from. |
| `thinking_level` | string \| null | no | "medium" | ["minimal", "medium", "high"] | How much the model reasons before generating. Higher levels can improve complex compositions and use more thinking tokens. |
| `limit_generations` | boolean | no | true | - | Experimental parameter to limit the number of generations from each round of prompting to 1. Set to `True` to to disregard any instructions in the prompt regarding the number of images to generate and ignore any intermediate images generated by the model. This may affect generation quality. |
| `system_prompt` | string [maxLength=50000] | no | "" | - | Optional system instruction that steers the model's persona and output style across the request. Leave blank to omit; when provided, it is sent as the system instruction to Gemini (or as a system message on OpenAI-compatible providers). |
| `enable_web_search` | boolean | no | false | - | Enable web search for the image generation task. This will allow the model to use the latest information from the web to generate the image. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |

**OUTPUT** (`NanoBanana21Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `description` | string | yes | - | - | The description of the generated images. |
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.height` | integer \| null | no | - | - | The height of the image |
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |

**Use in the editor:** Default `Generate` alternative for photoreal elements with legible text (gallery tags: typography, text rendering). Token-priced, `resolution` 1K/2K/4K, `thinking_level`.

**Gaps:** Token-based price: the canonical table is illustrative (short prompt, EXCLUDING thinking and web-search tokens), so a real call costs somewhat more; `thinking_level` defaults to `medium` and bills as output text. No pixel-size parameter, only `aspect_ratio` and `resolution`. Output `images[]` width/height are nullable.


### `openai/gpt-image-2.5/sunburst/edit`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "GPT Image 2.5 Sunburst Edit ", category `image-to-image`, tags ["stylized", "transform", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text tokens (per 1M): **$5.00** input, **$1.25** cached, **$10.00** output. Image tokens (per 1M): **$8.00** input, **$2.00** cached, **$30.00** output. Changing the **quality** parameter significantly affects cost; by default we use **high**. Adjust it to your preference. See the description at the bottom of this page for more details on how much canonical image sizes cost. Total cost is rounded up to the closest hundredth of a cent ($0.0001)." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/openai/gpt-image-2.5/sunburst/edit>
  - Canonical-size price table on the model page (verbatim):

    | Size | low | medium | high | xhigh | max |
    |---|---|---|---|---|---|
    | 1024×768 | $0.00402 | $0.00903 | $0.03612 | $0.06420 | $0.14445 |
    | 1024×1024 | $0.00588 | $0.01317 | $0.05268 | $0.09366 | $0.21072 |
    | 1024×1536 | $0.00474 | $0.01029 | $0.04116 | $0.07377 | $0.16464 |
    | 1920×1080 | $0.00441 | $0.01029 | $0.03960 | $0.07041 | $0.15840 |
    | 2560×1440 | $0.00615 | $0.01434 | $0.05529 | $0.09828 | $0.22110 |
    | 3840×2160 | $0.01113 | $0.02595 | $0.10008 | $0.17790 | $0.40026 |

- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=openai/gpt-image-2.5/sunburst/edit>  (input schema `GptImage25SunburstEditInput`, output schema `GptImage25SunburstEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GptImage25SunburstEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `quality` | string | no | "high" | ["auto", "low", "medium", "high", "xhigh", "max"] | Quality for the generated image. Higher settings increase detail, latency, and token usage. Use 'auto' to let the model choose. |
| `mask_url` | string \| null | no | - | - | The URL of the mask image to use for the generation. This indicates what part of the image to edit. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string [minLength=1, maxLength=32000] | yes | - | - | The prompt for image generation |
| `image_size` | ImageSize (object) \| string | no | "auto" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto"] | The size of the generated image. Use 'auto' to infer from input images. |
| `num_images` | integer [minimum=1, maximum=10] | no | 1 | - | Number of images to generate |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `output_compression` | integer [minimum=0, maximum=100] \| null | no | - | - | Compression level from 0 to 100. Only supported when output_format is 'jpeg' or 'webp'. |
| `image_urls` | array<string> [maxItems=16] | yes | - | - | The URLs of the images to use as a reference for the generation. A maximum of 16 images are allowed. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`GptImage25SunburstEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Newest GPT Image edit family, alternative instruction editor (up to 16 reference images, optional `background: transparent`).

**Gaps:** Token-priced: the page's canonical-size table is per `quality` and per size; `image_size` 'auto' infers from the inputs, so the billed size is not known in advance. The page does not say what distinguishes `sunburst` from `flare` (their schemas, prices and tables are identical). `mask_url` polarity is not stated ('indicates what part of the image to edit'); this client does not wire it. No seed field.


### `openai/gpt-image-2.5/flare/edit`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "GPT Image 2.5 Flare Edit", category `image-to-image`, tags ["stylized", "transform", "editing"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Text tokens (per 1M): **$5.00** input, **$1.25** cached, **$10.00** output. Image tokens (per 1M): **$8.00** input, **$2.00** cached, **$30.00** output. Changing the **quality** parameter significantly affects cost; by default we use **high**. Adjust it to your preference. See the description at the bottom of this page for more details on how much canonical image sizes cost. Total cost is rounded up to the closest hundredth of a cent ($0.0001)." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 1, `is_partner_api` = true - <https://fal.ai/models/openai/gpt-image-2.5/flare/edit>
  - Canonical-size price table on the model page (verbatim):

    | Size | low | medium | high | xhigh | max |
    |---|---|---|---|---|---|
    | 1024×768 | $0.00402 | $0.00903 | $0.03612 | $0.06420 | $0.14445 |
    | 1024×1024 | $0.00588 | $0.01317 | $0.05268 | $0.09366 | $0.21072 |
    | 1024×1536 | $0.00474 | $0.01029 | $0.04116 | $0.07377 | $0.16464 |
    | 1920×1080 | $0.00441 | $0.01029 | $0.03960 | $0.07041 | $0.15840 |
    | 2560×1440 | $0.00615 | $0.01434 | $0.05529 | $0.09828 | $0.22110 |
    | 3840×2160 | $0.01113 | $0.02595 | $0.10008 | $0.17790 | $0.40026 |

- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=openai/gpt-image-2.5/flare/edit>  (input schema `GptImage25FlareEditInput`, output schema `GptImage25FlareEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`GptImage25FlareEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `quality` | string | no | "high" | ["auto", "low", "medium", "high", "xhigh", "max"] | Quality for the generated image. Higher settings increase detail, latency, and token usage. Use 'auto' to let the model choose. |
| `mask_url` | string \| null | no | - | - | The URL of the mask image to use for the generation. This indicates what part of the image to edit. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string [minLength=1, maxLength=32000] | yes | - | - | The prompt for image generation |
| `image_size` | ImageSize (object) \| string | no | "auto" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto"] | The size of the generated image. Use 'auto' to infer from input images. |
| `num_images` | integer [minimum=1, maximum=10] | no | 1 | - | Number of images to generate |
| `background` | string | no | "auto" | ["auto", "transparent", "opaque"] | Background for the generated image |
| `output_compression` | integer [minimum=0, maximum=100] \| null | no | - | - | Compression level from 0 to 100. Only supported when output_format is 'jpeg' or 'webp'. |
| `image_urls` | array<string> [maxItems=16] | yes | - | - | The URLs of the images to use as a reference for the generation. A maximum of 16 images are allowed. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | Output format for the images |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`GptImage25FlareEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<ImageFile (object)> | yes | - | - | The generated images. |

*Nested `ImageFile` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.width` | integer \| null | no | - | - | The width of the image |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.height` | integer \| null | no | - | - | The height of the image |

**Use in the editor:** Same as `openai/gpt-image-2.5/sunburst/edit`.

**Gaps:** Identical to `openai/gpt-image-2.5/sunburst/edit`; the difference between the two variants is not documented on either page.


### `bytedance/seedream/v5/pro/edit`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Seedream 5.0 Pro Image Editing", category `image-to-image`, tags ["realism", "typography", "stylized"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost **$0.03375 per generated layer** for total pixel area under 1536x1536. For total pixel area over 1536x1536, each request will cost **$0.0675 per generated layer** . Please note that total pixel area is determined from the size of the generated base layer." - <https://fal.ai/models/bytedance/seedream/v5/pro/edit>
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "You will be charged for both input and output images. The first input image is not charged, and every additional input image will cost **$0.0045**. Tentative pricing is **$0.0675+ $(0.0045 x number of additional input images)** per output image for images of total area less than or equal to **1536x1536** pixels. For output images with area in between **1536x1536** and **2048x2048** pixels, the price is **$0.135 + $(0.0045 x number of additional input images)** per image." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.0675, `is_partner_api` = true - <https://fal.ai/models/bytedance/seedream/v5/pro/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bytedance/seedream/v5/pro/edit>  (input schema `SeedreamV5ProEditInput`, output schema `SeedreamV5ProEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`SeedreamV5ProEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `image_urls` | array<string> | yes | - | - | List of URLs of input reference images. Up to 10 images are supported; if more are sent, only the last 10 are used. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | Number of separate model generations to run with the prompt. |
| `prompt` | string | yes | - | - | The text prompt used to edit the image. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The file format of the generated image. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `image_size` | ImageSize (object) \| string | no | "auto_2K" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto_1K", "auto_2K"] | The size of the generated image. Total pixels must be between 1024x1024 and 2048x2048, with aspect ratio between 1/16 and 16. |
| `enhance_prompt_mode` | string | no | "standard" | ["standard", "fast"] | Prompt optimization mode. `standard` prioritizes image quality; `fast` reduces generation time. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`SeedreamV5ProEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated images. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |

**Use in the editor:** Seedream 5 Pro instruction edit (up to 10 reference images; gallery tags: realism, typography).

**Gaps:** The two price statements disagree by 2x: the page sentence says $0.03375 per generated layer under 1536x1536 and $0.0675 over it; the gallery listing says 'Tentative pricing' of $0.0675 (+ $0.0045 per additional input image) up to 1536x1536 and $0.135 above; the billing metadata is 0.0675. The schema default `image_size` is `auto_2K` (above the lower tier). No seed field.


### `bytedance/seedream/v5/pro/text-to-image`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Seedream 5.0 Pro Text to Image", category `text-to-image`, tags ["realism", "typography", "stylized"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=text-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost **$0.03375 per generated layer** for total pixel area under 1536x1536. For total pixel area over 1536x1536, each request will cost **$0.0675 per generated layer** . Please note that total pixel area is determined from the size of the generated base layer." - <https://fal.ai/models/bytedance/seedream/v5/pro/text-to-image>
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Tentative pricing is **$0.0675** per image for images of total area less than or equal to **1536x1536** pixels. For images with area in between **1536x1536** and **2048x2048** pixels, the price is **$0.135** per image." - <https://fal.ai/api/models?categories=text-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.0675, `is_partner_api` = true - <https://fal.ai/models/bytedance/seedream/v5/pro/text-to-image>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bytedance/seedream/v5/pro/text-to-image>  (input schema `SeedreamV5ProTextToImageInput`, output schema `SeedreamV5ProTextToImageOutput`)
- Category (x-fal-metadata): `text-to-image`

**INPUT** (`SeedreamV5ProTextToImageInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |
| `image_size` | ImageSize (object) \| string | no | "auto_2K" | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto_1K", "auto_2K"] | The size of the generated image. Total pixels must be between 1024x1024 and 2048x2048, with aspect ratio between 1/16 and 16. |
| `num_images` | integer [minimum=1, maximum=6] | no | 1 | - | Number of separate model generations to run with the prompt. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI and the output data won't be available in the request history. |
| `prompt` | string | yes | - | - | The text prompt used to generate the image. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | The file format of the generated image. |
| `enhance_prompt_mode` | string | no | "standard" | ["standard", "fast"] | Prompt optimization mode. `standard` prioritizes image quality; `fast` reduces generation time. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |

**OUTPUT** (`SeedreamV5ProTextToImageOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | Generated images. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |

**Use in the editor:** Seedream 5 Pro text-to-image alternative.

**Gaps:** Same 2x disagreement between the page sentence and the gallery listing as the edit endpoint; the schema default `image_size` is `auto_2K`. No seed field. Output width/height are nullable.


### `fal-ai/qwen-image-edit-2511`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Qwen Image Edit 2511", category `image-to-image`, tags ["stylized", "transform"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page copy served at <https://fal.ai/models/fal-ai/qwen-image-edit-2511/llms.txt> (verbatim): "- **Price**: $0.03 per megapixels For more details, see [fal.ai pricing](https://fal.ai/pricing)."
  - Billing metadata embedded in the model page: `billing_unit` = "megapixels", `price` = 0.03, `is_partner_api` = false - <https://fal.ai/models/fal-ai/qwen-image-edit-2511>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/qwen-image-edit-2511>  (input schema `QwenImageEdit2511Input`, output schema `QwenImageEdit2511Output`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`QwenImageEdit2511Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `seed` | integer \| null | no | - | - | The same seed and the same prompt given to the same version of the model will output the same image every time. |
| `sync_mode` | boolean | no | false | - | If `True`, the media will be returned as a data URI. |
| `image_size` | ImageSize (object) \| string \| null | no | - | ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"] | The size of the generated image. If None, uses the input image dimensions. |
| `output_format` | string | no | "png" | ["jpeg", "png", "webp"] | The format of the generated image. |
| `num_images` | integer [minimum=1, maximum=4] | no | 1 | - | The number of images to generate. |
| `num_inference_steps` | integer [minimum=1, maximum=50] | no | 28 | - | The number of inference steps to perform. |
| `acceleration` | string | no | "regular" | ["none", "regular", "high"] | The acceleration level to use. |
| `negative_prompt` | string | no | "" | - | The negative prompt to generate an image from. |
| `guidance_scale` | number [minimum=1, maximum=20] | no | 4.5 | - | The guidance scale to use for the image generation. |
| `prompt` | string | yes | - | - | The prompt to edit the image with. |
| `image_urls` | array<string> | yes | - | - | The URLs of the images to edit. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. Disabling it requires account authorization; unauthorized requests are always checked. |

*Nested `ImageSize` (at `image_size`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_size.height` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The height of the generated image. |
| `image_size.width` | integer [maximum=14142, exclusiveMinimum=0] | no | 512 | - | The width of the generated image. |

**OUTPUT** (`QwenImageEdit2511Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images` | array<Image (object)> | yes | - | - | The generated image files info. |
| `has_nsfw_concepts` | array<boolean> | yes | - | - | Whether the generated images contain NSFW concepts. |
| `prompt` | string | yes | - | - | The prompt used for generating the image. |
| `seed` | integer | yes | - | - | Seed of the generated Image. It will be the same value of the one passed in the input or the randomly generated that was used in case none was passed. |
| `timings` | object (object) | yes | - | - | - |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | - |
| `images.content_type` | string \| null | no | "image/jpeg" | - | - |
| `images.height` | integer | yes | - | - | - |
| `images.width` | integer | yes | - | - | - |

**Use in the editor:** Qwen instruction edit with negative prompt, seed and guidance; open-weight lineage.

**Gaps:** No price sentence and no gallery override: the only price is the billing metadata / llms.txt line '$0.03 per megapixels', and the page does not say whether input or output megapixels count. `image_size` is nullable with no default ('If None, uses the input image dimensions').


### `topaz/upscale/image/generative`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Topaz Upscale Image Generative", category `image-to-image`, tags ["upscale", "image"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page statement (verbatim): "Your request will cost **$0.08** per started 8 megapixels of output with Wonder 3 or Wonder 3.5, and **$0.08** per started 4 megapixels with Wonder, Wonder 2, Recover 3, Standard MAX, Redefine, Recovery, or Recovery V2. A 24 MP output therefore costs **$0.24** with Wonder 3/3.5 or **$0.48** with the other models." - <https://fal.ai/models/topaz/upscale/image/generative>
  - Gallery listing `pricingInfoOverride` (verbatim, newlines collapsed): "Your request will cost **$0.08** per started 8 megapixels of output with Wonder 3 or Wonder 3.5, and **$0.08** per started 4 megapixels with Wonder, Wonder 2, Recover 3, Standard MAX, Redefine, Recovery, or Recovery V2. A 24 MP output therefore costs **$0.24** with Wonder 3/3.5 or **$0.48** with the other models." - <https://fal.ai/api/models?categories=image-to-image>
  - Billing metadata embedded in the model page: `billing_unit` = "units", `price` = 0.01, `is_partner_api` = true - <https://fal.ai/models/topaz/upscale/image/generative>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=topaz/upscale/image/generative>  (input schema `UpscaleImageGenerativeInput`, output schema `UpscaleImageGenerativeOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`UpscaleImageGenerativeInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `face_enhancement_creativity` | number [minimum=0, maximum=1] | no | 0 | - | Creativity level for face enhancement. 0.0 means no creativity, 1.0 means maximum creativity. Ignored if face enhancement is disabled. |
| `texture` | integer [minimum=1, maximum=5] \| null | no | - | - | Texture detail level for generative upscaling (1-5). Applies to Redefine model only. |
| `subject_detection` | string | no | "All" | ["All", "Foreground", "Background"] | Subject detection mode for the image enhancement. Applies to Wonder 3, Recovery and Recovery V2 models. |
| `denoise` | number [minimum=0, maximum=1] \| null | no | - | - | Denoising level (0.0-1.0). Applies to Redefine model only. |
| `face_enhancement_strength` | number [minimum=0, maximum=1] | no | 0.8 | - | Strength of the face enhancement. 0.0 means no enhancement, 1.0 means maximum enhancement. Ignored if face enhancement is disabled. |
| `face_enhancement` | boolean | no | true | - | Whether to apply face enhancement to the image. |
| `output_format` | string | no | "jpeg" | ["jpeg", "png"] | Output format of the upscaled image. |
| `upscale_factor` | number [minimum=1, maximum=4] | no | 2 | - | Factor to upscale the image by (e.g. 2.0 doubles width and height) |
| `crop_to_fill` | boolean | no | false | - | - |
| `autoprompt` | boolean \| null | no | - | - | Enable automatic prompt generation for generative upscaling. Applies to Redefine model only. |
| `enhancement_strength` | string \| null | no | - | ["low", "medium", "high"] | Enhancement strength for generative upscaling. Applies to Wonder 3 and Wonder 3.5 models. When omitted, Topaz auto-configures it. |
| `detail` | number [minimum=0, maximum=1] \| null | no | - | - | Detail recovery level (0.0-1.0). Applies to Recovery V2 model only. |
| `image_url` | string [minLength=1] | yes | - | - | Url of the image to be upscaled |
| `sharpen` | number [minimum=0, maximum=1] \| null | no | - | - | Sharpening level (0.0-1.0). Applies to Redefine model only. |
| `creativity` | integer [minimum=1, maximum=6] \| null | no | - | - | Creativity level for generative upscaling (1-6). Higher values produce more creative/hallucinated details. Applies to Redefine model only. |
| `model` | string | no | "Wonder 3" | ["Wonder 3.5", "Wonder 3", "Wonder 2", "Wonder", "Recover 3", "Standard MAX", "Redefine", "Recovery V2", "Recovery"] | Generative upscaling model. Wonder 3.5 is the latest Wonder with finer detail and fewer repetitive patterns; Wonder 3 is Topaz's most advanced generative realism model (Wonder 2/Wonder are earlier generations); Recover 3 rebuilds natural detail; Standard MAX is high-quality precision-leaning upscaling; Redefine adds prompt-guided creative detail; Recovery and Recovery V2 rebuild extreme low-resolution images. |
| `prompt` | string [maxLength=1024] \| null | no | - | - | Text prompt to guide generative upscaling (max 1024 chars). Applies to Redefine model only. |

**OUTPUT** (`UpscaleImageGenerativeOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | File (object) | yes | - | - | - |

*Nested `File` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |

**Use in the editor:** Topaz generative upscalers (Wonder 3.5, Wonder 3, Redefine, ...) next to `topaz/upscale/image/precision`.

**Gaps:** Price is per started 8 MP of output for Wonder 3/3.5 and per started 4 MP for every other model, so it depends on the output size and the `model`. Several fields apply to one model only (`texture`, `denoise`, `autoprompt`, `creativity`, `sharpen`, `prompt`: Redefine; `detail`: Recovery V2; `enhancement_strength`: Wonder 3/3.5); this client forwards only the model-independent ones. Output `File` has no width/height.


### `bria/fibo-edit/relight`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Fibo Edit [Relight]", category `image-to-image`, tags ["bria", "fibo-edit", "relighting", "json"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page copy served at <https://fal.ai/models/bria/fibo-edit/relight/llms.txt> (verbatim): "- **Price**: $0.04 per images For more details, see [fal.ai pricing](https://fal.ai/pricing)."
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/bria/fibo-edit/relight>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bria/fibo-edit/relight>  (input schema `FiboEditRelightInput`, output schema `FiboEditRelightOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FiboEditRelightInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The source image. |
| `light_type` | string | yes | - | ["midday", "blue hour light", "low-angle sunlight", "sunrise light", "spotlight on subject", "overcast light", "soft overcast daylight lighting", "cloud-filtered lighting", "fog-diffused lighting", "moonlight lighting", "starlight nighttime", "soft bokeh lighting", "harsh studio lighting"] | The quality/style/time of day. |
| `light_direction` | string \| null | yes | - | ["front", "side", "bottom", "top-down"] | Where the light comes from. |

**OUTPUT** (`FiboEditRelightOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Represents an image file. |
| `vgl` | object (object) | yes | - | - | The VGL document the image was generated from. |
| `images` | array<Image (object)> | no | [] | - | Generated images. |
| `structured_instruction` | object (object) | yes | - | - | Deprecated, use `vgl`. The same document under the old name. |

*Nested `Image` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Bria FIBO relight: 13 `light_type` values and 4 `light_direction` values, both required.

**Gaps:** No price sentence on the page: the only price is the billing metadata (`billing_unit` images, `price` 0.04). `light_direction` is typed `string | null` yet listed required. The output also carries a `vgl` document and a deprecated `structured_instruction`.


### `bria/fibo-edit/edit`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Fibo Edit", category `image-to-image`, tags ["bria", "fibo-edit", "image-editing", "json"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page copy served at <https://fal.ai/models/bria/fibo-edit/edit/llms.txt> (verbatim): "- **Price**: $0.04 per images For more details, see [fal.ai pricing](https://fal.ai/pricing)."
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/bria/fibo-edit/edit>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bria/fibo-edit/edit>  (input schema `FiboEditEditInput`, output schema `FiboEditEditOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FiboEditEditInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string \| null | no | - | - | Reference image (file or URL). |
| `mask_url` | string \| null | no | - | - | Mask image (file or URL). Optional |
| `new_vgl` | Vgl (object) \| null | no | - | - | The VGL of the image as it should end up. Requires `original_vgl`. |
| `vgl` | Vgl (object) \| null | no | - | - | A pre-built VGL document, edited from verbatim instead of building one from `instruction`. Send this or `instruction`, not both. |
| `negative_prompt` | string | no | "" | - | Negative prompt for image generation. |
| `seed` | integer | no | 5555 | - | Random seed for reproducibility. |
| `steps_num` | integer [minimum=20, maximum=50] | no | 30 | - | Number of inference steps. |
| `guidance_scale` | number \| integer | no | 5 | - | Guidance scale for text. |
| `original_vgl` | Vgl (object) \| null | no | - | - | The VGL of the image as it is now. With `new_vgl`, edits by the difference between the two. |
| `instruction` | string \| null | no | - | - | Instruction for image editing. |
| `sync_mode` | boolean | no | false | - | If true, returns the image directly in the response (increases latency). |

*Nested `Vgl` (at `new_vgl`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `new_vgl.objects` | array<PromptObject (object)> \| null | no | [] | - | A list of objects in the image to be generated, along with their attributes and relationships to other objects in the image. |
| `new_vgl.aesthetics` | Aesthetics (object) \| null | no | - | - | The aesthetics of the image to be generated. |
| `new_vgl.style_medium` | string \| null | no | - | - | The style medium of the image to be generated. |
| `new_vgl.edit_instruction` | string \| null | no | - | - | The edit instruction for the image. |
| `new_vgl.text_render` | array<?> \| null | no | [] | - | A list of text to be rendered in the image. |
| `new_vgl.short_description` | string \| null | no | - | - | A short description of the image to be generated. |
| `new_vgl.context` | string \| null | no | - | - | The context of the image to be generated. |
| `new_vgl.photographic_characteristics` | PhotographicCharacteristics (object) \| null | no | - | - | The photographic characteristics of the image to be generated. |
| `new_vgl.background_setting` | string \| null | no | - | - | The background setting of the image to be generated. |
| `new_vgl.lighting` | Lighting (object) \| null | no | - | - | The lighting of the image to be generated. |
| `new_vgl.artistic_style` | string \| null | no | - | - | The artistic style of the image to be generated. |

*Nested `Aesthetics` (at `new_vgl.aesthetics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `new_vgl.aesthetics.color_scheme` | string \| null | no | - | - | The color scheme of the image to be generated. |
| `new_vgl.aesthetics.composition` | string \| null | no | - | - | The composition of the image to be generated. |
| `new_vgl.aesthetics.preference_score` | string \| null | no | - | - | The preference score of the image to be generated. |
| `new_vgl.aesthetics.aesthetic_score` | string \| null | no | - | - | The aesthetic score of the image to be generated. |
| `new_vgl.aesthetics.mood_atmosphere` | string \| null | no | - | - | The mood and atmosphere of the image to be generated. |

*Nested `PhotographicCharacteristics` (at `new_vgl.photographic_characteristics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `new_vgl.photographic_characteristics.depth_of_field` | string \| null | no | - | - | The depth of field in the image to be generated. |
| `new_vgl.photographic_characteristics.camera_angle` | string \| null | no | - | - | The angle of the camera in the image to be generated. |
| `new_vgl.photographic_characteristics.focus` | string \| null | no | - | - | The focus in the image to be generated. |
| `new_vgl.photographic_characteristics.lens_focal_length` | string \| null | no | - | - | The focal length of the lens in the image to be generated. |

*Nested `Lighting` (at `new_vgl.lighting`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `new_vgl.lighting.conditions` | string \| null | no | - | - | The conditions of the lighting in the image to be generated. |
| `new_vgl.lighting.shadows` | string \| null | no | - | - | The shadows in the image to be generated. |
| `new_vgl.lighting.direction` | string \| null | no | - | - | The direction of the lighting in the image to be generated. |

*Nested `Vgl` (at `vgl`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `vgl.objects` | array<PromptObject (object)> \| null | no | [] | - | A list of objects in the image to be generated, along with their attributes and relationships to other objects in the image. |
| `vgl.aesthetics` | Aesthetics (object) \| null | no | - | - | The aesthetics of the image to be generated. |
| `vgl.style_medium` | string \| null | no | - | - | The style medium of the image to be generated. |
| `vgl.edit_instruction` | string \| null | no | - | - | The edit instruction for the image. |
| `vgl.text_render` | array<?> \| null | no | [] | - | A list of text to be rendered in the image. |
| `vgl.short_description` | string \| null | no | - | - | A short description of the image to be generated. |
| `vgl.context` | string \| null | no | - | - | The context of the image to be generated. |
| `vgl.photographic_characteristics` | PhotographicCharacteristics (object) \| null | no | - | - | The photographic characteristics of the image to be generated. |
| `vgl.background_setting` | string \| null | no | - | - | The background setting of the image to be generated. |
| `vgl.lighting` | Lighting (object) \| null | no | - | - | The lighting of the image to be generated. |
| `vgl.artistic_style` | string \| null | no | - | - | The artistic style of the image to be generated. |

*Nested `Aesthetics` (at `vgl.aesthetics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `vgl.aesthetics.color_scheme` | string \| null | no | - | - | The color scheme of the image to be generated. |
| `vgl.aesthetics.composition` | string \| null | no | - | - | The composition of the image to be generated. |
| `vgl.aesthetics.preference_score` | string \| null | no | - | - | The preference score of the image to be generated. |
| `vgl.aesthetics.aesthetic_score` | string \| null | no | - | - | The aesthetic score of the image to be generated. |
| `vgl.aesthetics.mood_atmosphere` | string \| null | no | - | - | The mood and atmosphere of the image to be generated. |

*Nested `PhotographicCharacteristics` (at `vgl.photographic_characteristics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `vgl.photographic_characteristics.depth_of_field` | string \| null | no | - | - | The depth of field in the image to be generated. |
| `vgl.photographic_characteristics.camera_angle` | string \| null | no | - | - | The angle of the camera in the image to be generated. |
| `vgl.photographic_characteristics.focus` | string \| null | no | - | - | The focus in the image to be generated. |
| `vgl.photographic_characteristics.lens_focal_length` | string \| null | no | - | - | The focal length of the lens in the image to be generated. |

*Nested `Lighting` (at `vgl.lighting`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `vgl.lighting.conditions` | string \| null | no | - | - | The conditions of the lighting in the image to be generated. |
| `vgl.lighting.shadows` | string \| null | no | - | - | The shadows in the image to be generated. |
| `vgl.lighting.direction` | string \| null | no | - | - | The direction of the lighting in the image to be generated. |

*Nested `Vgl` (at `original_vgl`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `original_vgl.objects` | array<PromptObject (object)> \| null | no | [] | - | A list of objects in the image to be generated, along with their attributes and relationships to other objects in the image. |
| `original_vgl.aesthetics` | Aesthetics (object) \| null | no | - | - | The aesthetics of the image to be generated. |
| `original_vgl.style_medium` | string \| null | no | - | - | The style medium of the image to be generated. |
| `original_vgl.edit_instruction` | string \| null | no | - | - | The edit instruction for the image. |
| `original_vgl.text_render` | array<?> \| null | no | [] | - | A list of text to be rendered in the image. |
| `original_vgl.short_description` | string \| null | no | - | - | A short description of the image to be generated. |
| `original_vgl.context` | string \| null | no | - | - | The context of the image to be generated. |
| `original_vgl.photographic_characteristics` | PhotographicCharacteristics (object) \| null | no | - | - | The photographic characteristics of the image to be generated. |
| `original_vgl.background_setting` | string \| null | no | - | - | The background setting of the image to be generated. |
| `original_vgl.lighting` | Lighting (object) \| null | no | - | - | The lighting of the image to be generated. |
| `original_vgl.artistic_style` | string \| null | no | - | - | The artistic style of the image to be generated. |

*Nested `Aesthetics` (at `original_vgl.aesthetics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `original_vgl.aesthetics.color_scheme` | string \| null | no | - | - | The color scheme of the image to be generated. |
| `original_vgl.aesthetics.composition` | string \| null | no | - | - | The composition of the image to be generated. |
| `original_vgl.aesthetics.preference_score` | string \| null | no | - | - | The preference score of the image to be generated. |
| `original_vgl.aesthetics.aesthetic_score` | string \| null | no | - | - | The aesthetic score of the image to be generated. |
| `original_vgl.aesthetics.mood_atmosphere` | string \| null | no | - | - | The mood and atmosphere of the image to be generated. |

*Nested `PhotographicCharacteristics` (at `original_vgl.photographic_characteristics`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `original_vgl.photographic_characteristics.depth_of_field` | string \| null | no | - | - | The depth of field in the image to be generated. |
| `original_vgl.photographic_characteristics.camera_angle` | string \| null | no | - | - | The angle of the camera in the image to be generated. |
| `original_vgl.photographic_characteristics.focus` | string \| null | no | - | - | The focus in the image to be generated. |
| `original_vgl.photographic_characteristics.lens_focal_length` | string \| null | no | - | - | The focal length of the lens in the image to be generated. |

*Nested `Lighting` (at `original_vgl.lighting`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `original_vgl.lighting.conditions` | string \| null | no | - | - | The conditions of the lighting in the image to be generated. |
| `original_vgl.lighting.shadows` | string \| null | no | - | - | The shadows in the image to be generated. |
| `original_vgl.lighting.direction` | string \| null | no | - | - | The direction of the lighting in the image to be generated. |

**OUTPUT** (`FiboEditEditOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Represents an image file. |
| `vgl` | object (object) | yes | - | - | The VGL document the image was generated from. |
| `images` | array<Image (object)> | no | [] | - | Generated images. |
| `structured_instruction` | object (object) | yes | - | - | Deprecated, use `vgl`. The same document under the old name. |

*Nested `Image` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Bria FIBO instruction edit (`instruction` + `image_url`); optional `mask_url` and structured `vgl` documents exist but are not forwarded by this client.

**Gaps:** No price sentence (billing metadata 0.04 per image). `image_url` is not marked required although an edit needs it. `mask_url` polarity is not stated. `seed` defaults to the fixed value 5555. `guidance_scale` is `number | integer` with no bounds.


### `bria/fibo-edit/erase_by_text`

- **Status: established** (added by the owner directive of 2026-10-10 to use the newest model families; supersedes an older default in the v1 table); schema HTTP 200.
- Gallery entry: title "Fibo Edit [Erase by Text]", category `image-to-image`, tags ["bria", "fibo-edit", "prompt-eraser"], `licenseType` "commercial", `deprecated` false - <https://fal.ai/api/models?categories=image-to-image>
- Price:
  - Page copy served at <https://fal.ai/models/bria/fibo-edit/erase_by_text/llms.txt> (verbatim): "- **Price**: $0.04 per images For more details, see [fal.ai pricing](https://fal.ai/pricing)."
  - Billing metadata embedded in the model page: `billing_unit` = "images", `price` = 0.04, `is_partner_api` = true - <https://fal.ai/models/bria/fibo-edit/erase_by_text>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=bria/fibo-edit/erase_by_text>  (input schema `FiboEditErase_by_textInput`, output schema `FiboEditErase_by_textOutput`)
- Category (x-fal-metadata): `image-to-image`

**INPUT** (`FiboEditErase_by_textInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The source image. |
| `object_name` | string | yes | - | - | The name of the object to remove. |

**OUTPUT** (`FiboEditErase_by_textOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image` | Image (object) | yes | - | - | Represents an image file. |
| `vgl` | object (object) | yes | - | - | The VGL document the image was generated from. |
| `images` | array<Image (object)> | no | [] | - | Generated images. |
| `structured_instruction` | object (object) | yes | - | - | Deprecated, use `vgl`. The same document under the old name. |

*Nested `Image` (at `image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `image.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `image.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `image.content_type` | string \| null | no | - | - | The mime type of the file. |
| `image.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `image.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

*Nested `Image` (at `images`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `images.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `images.height` | integer \| null | no | - | - | The height of the image in pixels. |
| `images.file_name` | string \| null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `images.content_type` | string \| null | no | - | - | The mime type of the file. |
| `images.width` | integer \| null | no | - | - | The width of the image in pixels. |
| `images.file_size` | integer \| null | no | - | - | The size of the file in bytes. |

**Use in the editor:** Erase an object NAMED in words (`object_name`), no mask.

**Gaps:** No price sentence (billing metadata 0.04 per image). How the object is located and whether several instances are all removed is not stated.


## 10. Video generation - image-to-video and text-to-video (added 2026-10-16)

Everything below was fetched on 2026-10-16 from the same two surfaces the image survey used: each
endpoint's own OpenAPI document (`fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}`) and its model page
(`fal.ai/models/{id}`, whose `llms.txt` renders the same pricing sentence). The gallery listings that decide whether an
id exists at all are <https://fal.ai/api/models?categories=image-to-video> and
<https://fal.ai/api/models?categories=text-to-video>. The queue contract itself is
<https://fal.ai/docs/documentation/model-apis/inference/queue.md>.

### The tracking URLs are APP-scoped, and fal's own documentation shows the wrong shape

POST `https://queue.fal.run/{id}` answers a `QueueStatus` carrying `status`, `request_id`, `status_url`,
`response_url`, `cancel_url`, `logs`, `metrics` and `queue_position`; the status vocabulary is exactly
`IN_QUEUE` / `IN_PROGRESS` / `COMPLETED`, and `error` on a COMPLETED document means the request FAILED. The one
thing worth measuring rather than reading, because the published example is wrong:

- fal's queue page shows `status_url: https://queue.fal.run/fal-ai/flux/schnell/requests/{id}/status`, and every
  endpoint's OpenAPI declares `/{id}/requests/{request_id}/status` - the ENDPOINT-shaped URL.
- **Measured without a credential on 2026-10-16: that URL answers HTTP 405** (for
  `fal-ai/vidu/q4/image-to-video` and for `fal-ai/flux/schnell` alike), while the **app-scoped** URL
  `https://queue.fal.run/{owner}/{app}/requests/{id}/status` - where `{owner}/{app}` is the model id's FIRST TWO
  path segments - answers `404 {"status":"NOT_FOUND"}` for an unknown request id, and the result route
  `.../requests/{id}` answers `404 {"detail":"Request not found"}`. A wrong app guess
  (`fal-ai/vidu/q4/requests/{id}/status`) answers 405 too, which is how the shape was pinned down.
- A POST to an unrouted model answers `404 {"detail":"Application \"not-a-model\" not found"}`; a MISSING
  credential answers `401 {"detail":"Cannot access application \"fal-ai/vidu\". Authentication is required to
  access this application."}`; a present-but-invalid one answers `401 {"detail":"invalid key credentials"}` BEFORE
  the body is looked at (the same answer came back for `{}`, for malformed JSON and for an unrouted model id).

**Why this matters to the client:** the `VideoGenerate` step resumes a crashed take by polling its PERSISTED job
handle instead of submitting again (at these prices a second POST is a second clip), and a handle that
held only the request id could not rebuild these URLs. So `ExternalJobHandle.RemoteJobId` for this kind is
`{requestId}|{statusUrl}|{responseUrl}`, taken from the submit answer verbatim, and a poll after a restart
issues GETs only.

### The no-cost probe: the video arm does NOT send an empty body

`FalQueueClient.ProbeAsync` submits an EMPTY body on the premise that every configured endpoint marks at
least one input required, so nothing can be dispatched. **That premise is false for video:**
`fal-ai/kling-video/v3/turbo/pro/text-to-video` declares `required: []` with every input optional and
`generate_audio` defaulting to true, so `{}` is a VALID request - a real 5 s clip and a real $0.70 charge
(the same body on its image-to-video sibling is refused only because that schema requires a frame).
The video arm therefore submits `FalQueueClient.UnparseableProbeBody` (`{"reelboltProbe":`), which is not a
JSON document at all: no endpoint's input schema can be satisfied by it, because there is nothing to
deserialise, so no job can dispatch, generate or bill. A test asserts exactly that (the body fails
`JsonDocument.Parse`), and the classification is unchanged from the image arm: 404 = the model id is not
routed, 401/403 (or an authorization-shaped body) = the key was rejected, 400/422 = the endpoint exists
and the key got past authentication, a 2xx is followed to its result for up to 20 s and a job that
actually RAN fails closed.

### Image inputs travel as data URIs, with a caveat that is measured, not assumed

`VideoGenerationRequest.Images` carries bytes and this service holds no upload path of its own, so a frame
is sent as a base64 data URI. fal documents that form platform-wide - "Models also accept base64-encoded
data URIs", with an `image_url` example - while warning that a URL "is the universal format that works with
every model", and that a data URI "inflates the request size significantly, slows down transmission, and
is not recommended for files larger than a few KB"
(<https://fal.ai/docs/documentation/model-apis/fal-cdn.md>, read 2026-10-16). Only some endpoints
restate it per field: Vidu Q4's `image_url` reads "URL or base64 data URI of the image to use as the
first frame"; the others describe their field as a URL. Per-endpoint acceptance is on the
"must measure with a real key" list above - and so is whether a generated clip comes back when the frame
was a data URI rather than a CDN URL.

### Prices: the POST-promotion figure is the card

Where a page shows a promotional rate and the rate after it, the rate AFTER it is the card: Vidu Q4 is
30% off until November 30, MiniMax H3 Max Turbo is 40% off ending October 15. That is the conservative
direction (a take is never under-charged) and the same choice the image catalogue's FLUX.3 rows make.
Two endpoints price per second AND per something else this capability cannot express, and say so: Grok
Imagine Lite adds $0.01 per input image, and `fal-ai/veo3.1*` bills an audio tier that the schema's
`generate_audio: true` (never forwarded) puts every take in - so the cards are the WITH-audio figures.

### Retention: 30 days, and the platform does not silently opt out

"Request inputs and outputs (the JSON data) are stored in the platform for **30 days** by default" and
"To prevent fal from storing request payloads entirely, add the `X-Fal-Store-IO: 0` header"
(<https://fal.ai/docs/documentation/model-apis/media-expiration.md>, read 2026-10-16). **This client does
not send that header:** it changes what the vendor keeps, which is an operator's decision about their own
prompts and frames, not a default a client picks. Recorded here so the choice is visible rather than
implicit. The one exception is an avatar request (section 14): a person's media is never left with the
vendor, so those submits always carry `X-Fal-Store-IO: 0`.

### Deliberately not offered

- `bytedance/seedance-2.5/image-to-video` - its only per-second figure is the vendor's own "roughly" figure
  derived from a token formula ("roughly $0.2205 per second of generated video" beside "$0.0214 per 1000
  tokens ... roughly (height x width x (input duration + output duration) x 24) / 1024"), so there is no
  exact per-second rate to cite and a per-second card cannot express a token count. The same class of
  refusal as OpenAI's token-priced image models.
- `lightricks/ltx-2.5/*` - LTX-2's open-weights licence caps free commercial use at US$10M annual revenue,
  the same reason the ComfyUI arm refuses LTX-2.
- `fal-ai/ltx-video*` (the two 2024 ids, `licenseType` "research").

### The endpoints, one section each

### `fal-ai/vidu/q4/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Vidu Q4", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-10-07 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/vidu/q4/image-to-video>  (input schema `ViduQ4ImageToVideoInput`, output schema `ViduQ4ImageToVideoOutput`)
- Used by the platform as: the **default** image-to-video endpoint (<https://fal.ai/models/fal-ai/vidu/q4/image-to-video>).

**INPUT** (`ViduQ4ImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `resolution` | string | no | "720p" | "540p", "720p", "1080p", "2K", "4K" | Output video resolution |
| `seed` | integer | null | no | - | - | Random seed for reproducibility. If None, a random seed is chosen. |
| `duration` | integer minimum=3 maximum=16 | no | 5 | - | Duration of the video in seconds (3-16) |
| `image_url` | string | yes | - | - | URL or base64 data URI of the image to use as the first frame. Supports png, jpeg, jpg and webp up to 50 MB. |
| `prompt` | string maxLength=5000 | no | "" | - | Text prompt for video generation, max 5000 characters |

**OUTPUT** (`ViduQ4ImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `cover_image` | object | null | no | - | - | Cover image of the generated video |
| `video` | ? | yes | - | - | The generated video from image using the Q4 model |
| `seed` | integer | yes | - | - | The random seed used for generation |

*Nested `Image` (at `cover_image`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `cover_image.width` | integer | null | no | - | - | The width of the image in pixels. |
| `cover_image.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `cover_image.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `cover_image.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `cover_image.height` | integer | null | no | - | - | The height of the image in pixels. |
| `cover_image.content_type` | string | null | no | - | - | The mime type of the file. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `image_url` (required); end frame -> none documented; prompt -> `prompt` (optional, max 5000); duration -> a JSON integer, 3..16; resolution -> `resolution`; ratio -> NO FIELD (the canvas follows the first frame, so the platform's own token `native` is the only ratio it offers and nothing is sent).

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.0315** at **540p**, **$0.0665** at **720p**, **$0.084** at **1080p**, **$0.133** at **2K** or **$0.273** at **4K**. For example, a **5s** video at **720p** (the default) will cost **$0.3325**. Note: these are promotional rates, **30%** off until **November 30**, after which **540p** is **$0.045**/second, **720p** is **$0.095**/second, **1080p** is **$0.12**/second, **2K** is **$0.19**/second and **4K** is **$0.39**/second. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/fal-ai/vidu/q4/image-to-video> (the same sentence is rendered by <https://fal.ai/models/fal-ai/vidu/q4/image-to-video/llms.txt>)

### `minimax/h3-max-turbo/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "H3 Max Turbo Image to Video", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-09-02 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=minimax/h3-max-turbo/image-to-video>  (input schema `H3MaxTurboImageToVideoInput`, output schema `H3MaxTurboImageToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/minimax/h3-max-turbo/image-to-video>).

**INPUT** (`H3MaxTurboImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `prompt_expansion_mode` | string | yes | "balanced" | - | How much effort to spend rewriting the prompt before generation. 'disabled' skips prompt expansion. 'balanced' returns in about a second. 'quality' spends up to ~30s on a richer prompt. |
| `target_audio_url` | string | null minLength=1 | no | - | - | Optional URL of an audio clip at least 2 seconds long (maximum 15 MB) to pin to the generated soundtrack. Longer clips are trimmed to the generated video's length, keeping the beginning. The original audio replaces the output soundtrack, padded with silence if shorter than the video, without changing playback speed. Exceptionally high sample rates may be resampled to 96 kHz. Accepts an HTTP(S) URL or a base64 data URI. |
| `sync_mode` | boolean | no | false | - | Return the generated video as base64 instead of a CDN URL. |
| `image_url` | string | null | no | - | - | Optional URL of the image to use as the first frame. When provided, the output canvas follows this image. If only end_image_url is provided, the canvas follows that last frame instead. If both images are omitted, the request is handled as text-to-video (16:9 by default). |
| `seed` | integer | null | no | - | - | Random seed. A random seed is selected when omitted. |
| `prompt` | string minLength=1 maxLength=50000 | yes | - | - | Text prompt for video generation |
| `duration` | number minimum=0.92 maximum=15 | no | 5 | - | Video length in seconds. The output can run up to about 0.7 s longer than requested. Use at least 2 s with target_audio_url. |
| `end_image_url` | string | null | no | - | - | Optional URL of the image to use as the last frame. It may be provided alone for end-only keyframe generation; in that case the output canvas follows this image. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. |
| `resolution` | string | no | "768P" | "480P", "768P", "1080P" | The native generation resolution, or 1080P latent refinement from a native 768P source. |

**OUTPUT** (`H3MaxTurboImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video |
| `timings` | object | null | no | - | - | Timing breakdown in seconds. 'inference' is the DiT denoising time on the GPU backend. Null on routes that do not report backend timings. |
| `expanded_prompt` | string | null | no | - | - | The prompt after expansion, as sent to the model. Null when prompt expansion was disabled, left the prompt unchanged, or was performed internally by MiniMax's hosted API. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `image_url` (optional); end frame -> `end_image_url` (optional); prompt -> `prompt` (required, max 50000); duration -> a JSON number, 1..15; resolution -> `resolution`; ratio -> NO FIELD (token `native`); also sent by default: `prompt_expansion_mode` = `balanced` (the schema marks it required and defaults it).

- Price sentence (verbatim): Video costs **$0.015** per second at **480p**, **$0.024** per second at **768p**, and **$0.048** per second at **1080p**. Note: these are promotional rates, **40%** off for a limited time. The discount ends **October 15**, after which **480p** is **$0.025**/second, **768p** is **$0.04**/second, and **1080p** is **$0.08**/second. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/minimax/h3-max-turbo/image-to-video> (the same sentence is rendered by <https://fal.ai/models/minimax/h3-max-turbo/image-to-video/llms.txt>)

### `fal-ai/kling-video/v3/turbo/pro/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Kling Video V3 Turbo Pro Image to Video", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-06-17 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/kling-video/v3/turbo/pro/image-to-video>  (input schema `KlingVideoV3TurboProImageToVideoInput`, output schema `KlingVideoV3TurboProImageToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/image-to-video>).

**INPUT** (`KlingVideoV3TurboProImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | string | no | "5" | "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" | Video length in seconds. |
| `image_url` | string | yes | - | - | First-frame reference image. Formats: .jpg/.jpeg/.png; max 50MB; min 300px per side; aspect ratio within 1:2.5 to 2.5:1. |
| `multi_prompt` | array | null | no | - | - | Multi-shot storyboard (1-6 shots). Each shot has its own prompt and duration; the total duration must not exceed 15s. Mutually exclusive with `prompt`. |
| `prompt` | string | null maxLength=3072 | no | - | - | Optional text prompt. For best results keep the prompt under 2500 characters. Mutually exclusive with `multi_prompt`. |

*Nested `KlingV3MultiPromptElement` (at `multi_prompt[]`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `multi_prompt.duration` | string | no | "5" | "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" | The duration of this shot in seconds |
| `multi_prompt.prompt` | string | yes | - | - | The prompt for this shot. |

**OUTPUT** (`KlingVideoV3TurboProImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `image_url` (required); end frame -> none documented; prompt -> `prompt` (optional, max 3072); duration -> a JSON STRING holding a number, 3..15; resolution -> NO FIELD (one flat price, token `native`); ratio -> NO FIELD (token `native`).

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.14**. For example, a **5s** video will cost **$0.70**. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/image-to-video> (the same sentence is rendered by <https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/image-to-video/llms.txt>)

### `alibaba/wan-3.0/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Wan 3.0", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-08-24 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/wan-3.0/image-to-video>  (input schema `Wan30ImageToVideoInput`, output schema `Wan30ImageToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/alibaba/wan-3.0/image-to-video>).

**INPUT** (`Wan30ImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | integer | null minimum=2 maximum=30 | no | 5 | - | Output duration in seconds. Set to null for smart duration, which lets the model pick a length from the prompt and reference media. |
| `enable_safety_checker` | boolean | no | true | - | Enable content moderation for input and output. Disabling it requires account authorization; unauthorized requests are always checked. |
| `seed` | integer | null minimum=0 maximum=2147483647 | no | - | - |  |
| `enable_thinking` | boolean | no | false | - | Enable enhanced reasoning before generation. |
| `enable_prompt_expansion` | boolean | no | true | - | Enable intelligent prompt rewriting. Disabling it can save roughly 20-60 seconds of latency but is likely to degrade generation quality. |
| `end_image_url` | string | null | no | - | - | Last frame of the generated video. Requires start_image_url. |
| `audio` | boolean | no | true | - | Include generated audio. |
| `start_image_url` | string | yes | - | - | First frame of the generated video. |
| `resolution` | string | no | "1080p" | "480p", "720p", "1080p" | Output video resolution tier. |
| `prompt` | string | null maxLength=20000 | no | - | - | Text prompt describing the motion to generate. |
| `aspect_ratio` | string | no | "adaptive" | "adaptive", "16:9", "4:3", "1:1", "3:4", "9:16" | Output aspect ratio, or adaptive selection. |

**OUTPUT** (`Wan30ImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `actual_prompt` | string | null | no | - | - |  |
| `video` | ? | yes | - | - | The generated video file. |
| `duration` | number | yes | - | - | Generated video duration in seconds. |
| `seed` | integer | yes | - | - | The seed used for generation. |

*Nested `VideoFile` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.duration` | number | null | no | - | - | The duration of the video |
| `video.width` | integer | null | no | - | - | The width of the video |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.num_frames` | integer | null | no | - | - | The number of frames in the video |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.height` | integer | null | no | - | - | The height of the video |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.fps` | number | null | no | - | - | The FPS of the video |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `start_image_url` (required); end frame -> `end_image_url` (optional); prompt -> `prompt` (optional, max 20000); duration -> a JSON integer, 2..30; resolution -> `resolution`; ratio -> `aspect_ratio`.

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.05** **480p**, **$0.10** **720p**, or **$0.20** **1080p**. For example, a **5s** video at **720p** will cost **$0.50**. **Pricing is subjected to change.** For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/alibaba/wan-3.0/image-to-video> (the same sentence is rendered by <https://fal.ai/models/alibaba/wan-3.0/image-to-video/llms.txt>)

### `blackforestlabs/flux-3/first-last-frame-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Flux 3 First Last Frame to Video", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-08-04 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=blackforestlabs/flux-3/first-last-frame-to-video>  (input schema `Flux3FirstLastFrameToVideoInput`, output schema `Flux3FirstLastFrameToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/blackforestlabs/flux-3/first-last-frame-to-video>).

**INPUT** (`Flux3FirstLastFrameToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `start_image_url` | string | yes | - | - | URL of the image used as the first frame of the video (PNG, JPEG, or WebP). |
| `generate_audio` | boolean | no | true | - | Whether to generate audio for the video. |
| `safety_tolerance` | integer minimum=0 maximum=4 | no | 2 | - | The safety tolerance level for the generated video. 0 is the strictest and 4 is the most permissive. |
| `aspect_ratio` | string | no | "auto" | "auto", "21:9", "2:1", "16:9", "4:3", "1:1", "3:4", "9:16" | Aspect ratio of the generated video. `auto` lets the model choose. |
| `end_image_url` | string | yes | - | - | URL of the image used as the last frame of the video (PNG, JPEG, or WebP). |
| `duration` | string | no | 5 | "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20" | Duration of the generated video in seconds. An explicit duration is required to place the end frame. |
| `prompt` | string | yes | - | - | The text prompt describing the video you want to generate. |
| `resolution` | string | no | "720p" | "720p", "1080p" | Resolution of the generated video. |

**OUTPUT** (`Flux3FirstLastFrameToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `seed` | integer | yes | - | - | The seed used for the generation. |
| `video` | ? | yes | - | - | The generated video. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `start_image_url` (required); end frame -> `end_image_url` (REQUIRED); prompt -> `prompt` (required); duration -> a JSON integer, 5..20; resolution -> `resolution`; ratio -> `aspect_ratio`.

- Price sentence (verbatim): Your request will be charged at **0.17** $ per second of generated video at 720p, and **0.29** $ per second at 1080p. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/blackforestlabs/flux-3/first-last-frame-to-video> (the same sentence is rendered by <https://fal.ai/models/blackforestlabs/flux-3/first-last-frame-to-video/llms.txt>)

### `fal-ai/veo3.1/fast/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Veo 3.1 Fast", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2025-10-08 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1/fast/image-to-video>  (input schema `Veo31FastImageToVideoInput`, output schema `Veo31FastImageToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/fal-ai/veo3.1/fast/image-to-video>).

**INPUT** (`Veo31FastImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `seed` | integer | null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string | no | "auto" | "auto", "16:9", "9:16" | The aspect ratio of the generated video. Only 16:9 and 9:16 are supported. |
| `auto_fix` | boolean | no | false | - | Whether to automatically attempt to fix prompts that fail content policy or other validation checks by rewriting them. |
| `safety_tolerance` | string | no | "4" | "1", "2", "3", "4", "5", "6" | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `negative_prompt` | string | null | no | - | - | A negative prompt to guide the video generation. |
| `image_url` | string | yes | - | - | URL of the input image to animate. Should be 720p or higher resolution in 16:9 or 9:16 aspect ratio. If the image is not in 16:9 or 9:16 aspect ratio, it will be cropped to fit. |
| `resolution` | string | no | "720p" | "720p", "1080p", "4k" | The resolution of the generated video. |
| `generate_audio` | boolean | no | true | - | Whether to generate audio for the video. |
| `prompt` | string maxLength=20000 | yes | - | - | The text prompt describing the video you want to generate |
| `duration` | string | no | "8s" | "4s", "6s", "8s" | The duration of the generated video. |

**OUTPUT** (`Veo31FastImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `image_url` (required); end frame -> none documented; prompt -> `prompt` (required, max 20000); duration -> a JSON string holding seconds, one of 4s/6s/8s; resolution -> `resolution`; ratio -> `aspect_ratio`. `generate_audio` is NOT sent (schema default true), so the with-audio rate is what the take buys.

- Price sentence (verbatim): For every second of video you generate you will be charged **$0.10** without audio or **$0.15** with audio for 720p or 1080p. At 4k, you will be charged **$0.30** per second without audio, or **$0.35** with. For example, a **5 second video** at **1080p** with **audio on** will cost **$0.75**. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/fal-ai/veo3.1/fast/image-to-video> (the same sentence is rendered by <https://fal.ai/models/fal-ai/veo3.1/fast/image-to-video/llms.txt>)

### `xai/grok-imagine-video/v1.5/lite/image-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Grok Imagine Video 1.5 Lite Image to Video", category `image-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-10-01 - <https://fal.ai/api/models?categories=image-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=xai/grok-imagine-video/v1.5/lite/image-to-video>  (input schema `GrokImagineVideoV15LiteImageToVideoInput`, output schema `GrokImagineVideoV15LiteImageToVideoOutput`)
- Used by the platform as: the **alternative** image-to-video endpoint (<https://fal.ai/models/xai/grok-imagine-video/v1.5/lite/image-to-video>).

**INPUT** (`GrokImagineVideoV15LiteImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | integer minimum=1 maximum=15 | no | 6 | - | Video duration in seconds. |
| `image_url` | string | yes | - | - | URL of the input image for video generation. |
| `enable_safety_checker` | boolean | no | true | - | Authorized callers may set false to request relaxed screening. |
| `resolution` | string | no | "720p" | "480p", "720p", "1080p" | Resolution of the output video. |
| `prompt` | string maxLength=4096 | yes | - | - | Text description of desired changes or motion in the video. |

**OUTPUT** (`GrokImagineVideoV15LiteImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video. |

*Nested `VideoFile` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.duration` | number | null | no | - | - | The duration of the video |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.width` | integer | null | no | - | - | The width of the video |
| `video.fps` | number | null | no | - | - | The FPS of the video |
| `video.num_frames` | integer | null | no | - | - | The number of frames in the video |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.height` | integer | null | no | - | - | The height of the video |

**Wired by the platform** (`FalAiVideoCatalog`): first frame -> `image_url` (required); end frame -> none documented; prompt -> `prompt` (required, max 4096); duration -> a JSON integer, 1..15; resolution -> `resolution`; ratio -> NO FIELD (token `native`).

- Price sentence (verbatim): Priced per second of output video and per input image, by resolution: **480p** at **$0.02**/sec, **720p** at **$0.03**/sec, **1080p** at **$0.14**/sec. Each input image costs **$0.01**. A 5-second clip with one input image costs **$0.11** at 480p, **$0.16** at 720p, or **$0.71** at 1080p. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/xai/grok-imagine-video/v1.5/lite/image-to-video> (the same sentence is rendered by <https://fal.ai/models/xai/grok-imagine-video/v1.5/lite/image-to-video/llms.txt>)

### `fal-ai/kling-video/v3/turbo/pro/text-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Kling Video V3 Turbo Pro Text to Video", category `text-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-06-17 - <https://fal.ai/api/models?categories=text-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/kling-video/v3/turbo/pro/text-to-video>  (input schema `KlingVideoV3TurboProTextToVideoInput`, output schema `KlingVideoV3TurboProTextToVideoOutput`)
- Used by the platform as: the **default** text-to-video endpoint (<https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/text-to-video>).

**INPUT** (`KlingVideoV3TurboProTextToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | string | no | "5" | "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" | Video length in seconds. |
| `multi_prompt` | array | null | no | - | - | Multi-shot storyboard (1-6 shots). Each shot has its own prompt and duration; the total duration must not exceed 15s. Mutually exclusive with `prompt`. |
| `aspect_ratio` | string | no | "16:9" | "16:9", "9:16", "1:1" | The aspect ratio (width:height) of the generated video. |
| `prompt` | string | null maxLength=3072 | no | - | - | Text prompt to generate the video with. For best results keep the prompt under 2500 characters. Mutually exclusive with `multi_prompt`. |

*Nested `KlingV3MultiPromptElement` (at `multi_prompt[]`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `multi_prompt.duration` | string | no | "5" | "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" | The duration of this shot in seconds |
| `multi_prompt.prompt` | string | yes | - | - | The prompt for this shot. |

**OUTPUT** (`KlingVideoV3TurboProTextToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |

**Wired by the platform** (`FalAiVideoCatalog`): no image at all (this route refuses one); prompt -> `prompt` (the SCHEMA declares `required: []`, the platform still requires one - with no prompt the vendor's own default IS the request; max 3072); duration -> a JSON string holding a number, 3..15; resolution -> NO FIELD (token `native`); ratio -> `aspect_ratio`.

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.14** . For example, a **5s** video will cost **$0.70**. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/text-to-video> (the same sentence is rendered by <https://fal.ai/models/fal-ai/kling-video/v3/turbo/pro/text-to-video/llms.txt>)

### `minimax/h3-max-turbo/text-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "H3 Max Turbo Text to Video", category `text-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-09-02 - <https://fal.ai/api/models?categories=text-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=minimax/h3-max-turbo/text-to-video>  (input schema `H3MaxTurboTextToVideoInput`, output schema `H3MaxTurboTextToVideoOutput`)
- Used by the platform as: the **alternative** text-to-video endpoint (<https://fal.ai/models/minimax/h3-max-turbo/text-to-video>).

**INPUT** (`H3MaxTurboTextToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `aspect_ratio` | string | no | "16:9" | "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" | The aspect ratio of the generated video. |
| `prompt_expansion_mode` | string | yes | "balanced" | - | How much effort to spend rewriting the prompt before generation. 'disabled' skips prompt expansion. 'balanced' returns in about a second. 'quality' spends up to ~30s on a richer prompt. |
| `target_audio_url` | string | null minLength=1 | no | - | - | Optional URL of an audio clip at least 2 seconds long (maximum 15 MB) to pin to the generated soundtrack. Longer clips are trimmed to the generated video's length, keeping the beginning. The original audio replaces the output soundtrack, padded with silence if shorter than the video, without changing playback speed. Exceptionally high sample rates may be resampled to 96 kHz. Accepts an HTTP(S) URL or a base64 data URI. |
| `sync_mode` | boolean | no | false | - | Return the generated video as base64 instead of a CDN URL. |
| `seed` | integer | null | no | - | - | Random seed. A random seed is selected when omitted. |
| `prompt` | string minLength=1 maxLength=50000 | yes | - | - | Text prompt for video generation |
| `duration` | number minimum=0.92 maximum=15 | no | 5 | - | Video length in seconds. The output can run up to about 0.7 s longer than requested. Use at least 2 s with target_audio_url. |
| `resolution` | string | no | "768P" | "480P", "768P", "1080P" | The native generation resolution, or 1080P latent refinement from a native 768P source. |
| `enable_safety_checker` | boolean | no | true | - | If set to true, the safety checker will be enabled. |

**OUTPUT** (`H3MaxTurboTextToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video |
| `timings` | object | null | no | - | - | Timing breakdown in seconds. 'inference' is the DiT denoising time on the GPU backend. Null on routes that do not report backend timings. |
| `expanded_prompt` | string | null | no | - | - | The prompt after expansion, as sent to the model. Null when prompt expansion was disabled, left the prompt unchanged, or was performed internally by MiniMax's hosted API. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |

**Wired by the platform** (`FalAiVideoCatalog`): no image at all; prompt -> `prompt` (required, max 50000); duration -> a JSON number, 1..15; resolution -> `resolution`; ratio -> `aspect_ratio`; also sent by default: `prompt_expansion_mode` = `balanced`.

- Price sentence (verbatim): Video costs **$0.015** per second at **480p**, **$0.024** per second at **768p**, and **$0.048** per second at **1080p**. Note: these are promotional rates, **40%** off for a limited time. The discount ends **October 15**, after which **480p** is **$0.025**/second, **768p** is **$0.04**/second, and **1080p** is **$0.08**/second. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/minimax/h3-max-turbo/text-to-video> (the same sentence is rendered by <https://fal.ai/models/minimax/h3-max-turbo/text-to-video/llms.txt>)

### `alibaba/wan-3.0/text-to-video`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Wan Text to Video", category `text-to-video`, `licenseType` "commercial", `deprecated` false, published 2026-08-24 - <https://fal.ai/api/models?categories=text-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/wan-3.0/text-to-video>  (input schema `Wan30TextToVideoInput`, output schema `Wan30TextToVideoOutput`)
- Used by the platform as: the **alternative** text-to-video endpoint (<https://fal.ai/models/alibaba/wan-3.0/text-to-video>).

**INPUT** (`Wan30TextToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | integer | null minimum=2 maximum=30 | no | 5 | - | Output duration in seconds. Set to null for smart duration, which lets the model pick a length from the prompt and reference media. |
| `enable_safety_checker` | boolean | no | true | - | Enable content moderation for input and output. Disabling it requires account authorization; unauthorized requests are always checked. |
| `seed` | integer | null minimum=0 maximum=2147483647 | no | - | - |  |
| `enable_thinking` | boolean | no | false | - | Enable enhanced reasoning before generation. |
| `aspect_ratio` | string | no | "adaptive" | "adaptive", "16:9", "4:3", "1:1", "3:4", "9:16" | Output aspect ratio, or adaptive selection. |
| `enable_prompt_expansion` | boolean | no | true | - | Enable intelligent prompt rewriting. Disabling it can save roughly 20-60 seconds of latency but is likely to degrade generation quality. |
| `audio` | boolean | no | true | - | Include generated audio. |
| `resolution` | string | no | "1080p" | "480p", "720p", "1080p" | Output video resolution tier. |
| `prompt` | string minLength=1 maxLength=20000 | yes | - | - | Text prompt for the generated video. |

**OUTPUT** (`Wan30TextToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `actual_prompt` | string | null | no | - | - |  |
| `video` | ? | yes | - | - | The generated video file. |
| `duration` | number | yes | - | - | Generated video duration in seconds. |
| `seed` | integer | yes | - | - | The seed used for generation. |

*Nested `VideoFile` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.duration` | number | null | no | - | - | The duration of the video |
| `video.width` | integer | null | no | - | - | The width of the video |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.num_frames` | integer | null | no | - | - | The number of frames in the video |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.height` | integer | null | no | - | - | The height of the video |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.fps` | number | null | no | - | - | The FPS of the video |

**Wired by the platform** (`FalAiVideoCatalog`): no image at all; prompt -> `prompt` (required, max 20000); duration -> a JSON integer, 2..30; resolution -> `resolution`; ratio -> `aspect_ratio`.

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.05** **480p**, **$0.10** **720p**, or **$0.20** **1080p**. For example, a **5s** video at **720p** will cost **$0.50**. **Pricing is subjected to change.*** For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/alibaba/wan-3.0/text-to-video> (the same sentence is rendered by <https://fal.ai/models/alibaba/wan-3.0/text-to-video/llms.txt>)

### `fal-ai/veo3.1`

- **Status: established**; schema HTTP 200.
- Gallery entry: title "Veo 3.1", category `text-to-video`, `licenseType` "commercial", `deprecated` false, published 2025-10-08 - <https://fal.ai/api/models?categories=text-to-video>
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1>  (input schema `Veo31Input`, output schema `Veo31Output`)
- Used by the platform as: the **alternative** text-to-video endpoint (<https://fal.ai/models/fal-ai/veo3.1>).

**INPUT** (`Veo31Input`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `seed` | integer | null | no | - | - | The seed for the random number generator. |
| `aspect_ratio` | string | no | "16:9" | "16:9", "9:16" | Aspect ratio of the generated video |
| `auto_fix` | boolean | no | true | - | Whether to automatically attempt to fix prompts that fail content policy or other validation checks by rewriting them. |
| `safety_tolerance` | string | no | "4" | "1", "2", "3", "4", "5", "6" | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `negative_prompt` | string | null | no | - | - | A negative prompt to guide the video generation. |
| `resolution` | string | no | "720p" | "720p", "1080p", "4k" | The resolution of the generated video. |
| `generate_audio` | boolean | no | true | - | Whether to generate audio for the video. |
| `prompt` | string maxLength=20000 | yes | - | - | The text prompt describing the video you want to generate |
| `duration` | string | no | "8s" | "4s", "6s", "8s" | The duration of the generated video. |

**OUTPUT** (`Veo31Output`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video. |

*Nested `File` (at `video`)*

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video.file_size` | integer | null | no | - | - | The size of the file in bytes. |
| `video.url` | string | yes | - | - | The URL where the file can be downloaded from. |
| `video.file_name` | string | null | no | - | - | The name of the file. It will be auto-generated if not provided. |
| `video.content_type` | string | null | no | - | - | The mime type of the file. |

**Wired by the platform** (`FalAiVideoCatalog`): no image at all; prompt -> `prompt` (required, max 20000); duration -> a JSON string holding seconds, one of 4s/6s/8s; resolution -> `resolution`; ratio -> `aspect_ratio`. `generate_audio` is NOT sent (schema default true), so the with-audio rate is what the take buys.

- Price sentence (verbatim): For every second of video you generate you will be charged **$0.20** without audio or **$0.40** with audio for 720p or 1080p. At 4k, you will be charged **$0.40** per second without audio, or **$0.60** with. For example, a **5 second video** at **1080p** with **audio on** will cost **$2.00**. For more details, see [fal.ai pricing](https://fal.ai/pricing).
  - <https://fal.ai/models/fal-ai/veo3.1> (the same sentence is rendered by <https://fal.ai/models/fal-ai/veo3.1/llms.txt>)

## 11. Music and sound effects (added 2026-10-17)

Product decision 7 (superseding decision 2's second route): music and sound effects through fal **only** (this
section). The direct `ElevenLabs` provider kind (`docs/elevenlabs-provider.md`) is dormant: the admin boundary
refuses it every capability, nothing seeds it, and the resolver and client factory never use it. Facts come from the endpoint OpenAPI documents and `llms.txt` pricing
sentences read on 2026-10-17 (the initiative's audio fact sheet); **no call has been made with a key** - every
test replays recorded mocks (`inference/tests/ReelBolt.WorkflowEngine.Tests/Inference/Fixtures/Audio/`).

Wiring: capabilities `MusicGeneration` and `SoundEffectGeneration` on a `FalAi` row; `AudioModelCatalog`
(one default per job) and `FalAiAudioGenerationClient` over the shared `FalQueueClient` (submit, poll the
returned `status_url`, fetch the `response_url`, SSRF-guarded download with no `Authorization` header). Fields
are validated BEFORE submit: an undeclared field, an out-of-range value or an unoffered `output_format` is
refused and nothing is sent. Units: `MusicGenMinutes` (billed minutes) and `SoundEffectGenSeconds`. No output
carries a duration, so the engine probes the downloaded file with ffprobe (`AudioGenerationDurationProbe`).

| Endpoint | Job | Role | Price (vendor page) | Card |
|---|---|---|---|---|
| `fal-ai/elevenlabs/music` | Music | default | "$0.6 per output audio minute, rounded up to the next minute" | `MusicGenMinutes` $0.60, each started minute |
| `fal-ai/elevenlabs/sound-effects/v2` | Sound effect | default | "$0.002 per seconds" | `SoundEffectGenSeconds` $0.002, each started second |
| `fal-ai/mmaudio-v2/text-to-audio` | Sound effect | alternative | "$0.001 per second" | `SoundEffectGenSeconds` $0.001, each started second |

Not offered: `fal-ai/stable-audio-25/text-to-audio` ($0.2 flat per request, 190 s default: a per-duration card
cannot express it), `fal-ai/stable-audio` ("$0 per compute seconds", unpriced), and the SFX v1 endpoint (v2 has
the same price and adds `loop`).

### `fal-ai/elevenlabs/music`

- Request (every field optional in the schema; `{}` is schema-valid): `prompt` (max 4100) **xor**
  `composition_plan` (`MusicCompositionPlan`: `positive_global_styles[]`, `negative_global_styles[]`,
  `sections[]`, all required; section = `section_name` 1-100 chars, `positive_local_styles[]`,
  `negative_local_styles[]`, `duration_ms` 3000-120000, `lines[]` each at most 200 chars, all required);
  `music_length_ms` 3000-600000 (prompt only); `force_instrumental` (prompt only, default false);
  `respect_sections_durations` (plan only, default true; not sent); `output_format` (default `mp3_44100_128`).
  No `model_id`, no `seed`.
- What the client sends: prompt mode = `prompt`, `music_length_ms` (required by the PLATFORM, 3-300 s, so the
  take can be priced), optional `force_instrumental`, `output_format`. Plan mode = `composition_plan` (the
  caller's JSON, validated against the schema above, at most 30 sections, total 3-300 s) and `output_format`
  only. Both, neither, a plan with a duration or with instrumental: refused.
- Response: `{"audio": File}` (`url` required; `content_type`, `file_name`, `file_size` optional).

### `fal-ai/elevenlabs/sound-effects/v2`

- Request: `text` **required** (max 450); `duration_seconds` 0.5-22 or null (model picks); `prompt_influence`
  0-1 (default 0.3); `output_format`; `loop` (default false).
- Response: `{"audio": File}`; the recorded example carries only `url`, so the type and extension follow the
  `output_format` sent.

### `fal-ai/mmaudio-v2/text-to-audio`

- Request: `prompt` **required**; `negative_prompt`, `seed` 0-65535, `num_steps` 4-50 (25), `duration` 1-30 (8),
  `cfg_strength` 0-20 (4.5), `mask_away_clip`. The client sends `prompt` and `duration` only; no
  `output_format`, `loop` or `prompt_influence` field exists, so a request setting one is refused.
- Response: `{"audio": File}`, a `.flac` with `content_type: application/octet-stream` in the example
  (Foley-oriented; not a music model).

### Output formats offered

The ElevenLabs-family endpoints take a 19-value `output_format` enum. Only the MP3 and Opus tokens are offered
(`mp3_22050_32` ... `mp3_44100_192`, `opus_48000_32` ... `opus_48000_192`): `pcm_*`, `ulaw_8000` and
`alaw_8000` are raw samples ffprobe cannot read a duration from. 192 kbps MP3 is tier-gated by ElevenLabs;
how fal's own account maps to that is unconfirmed.

### Provider test

One authenticated `GET {base}/{owner}/{app}/requests/{random uuid}/status` on the row's app route
(`fal-ai/elevenlabs` or `fal-ai/mmaudio-v2`). Nothing is submitted, so nothing runs or bills; see row 28 of
the must-measure table.

### Licence

Music generated through ElevenLabs is governed by elevenlabs.io/music-terms (individual-use tiers, model
resale restricted); whether those terms bind calls routed through fal is **unconfirmed** (fal's pages state
none). Product decision 7 accepts fal's commercial terms as the basis for now and records the residual risk as
must-measure row 34 (**must confirm**: ElevenLabs terms may still apply to their models used via fal). See
`docs/elevenlabs-provider.md` "Licence".

## 12. Audio isolation (added 2026-11-02)

Hosted voice isolation (`InferenceProviderCapability.AudioIsolation`, usage kind `UsageKind.AudioCleanupMinutes`,
client `FalAiAudioIsolationClient`, catalogue `AudioIsolationModelCatalog`). **Nothing below was run with a key**: the
OpenAPI documents and `llms.txt` pages were READ on 2026-11-02; every behaviour is UNVERIFIED until the table at the end
is measured. Tests replay hand-written fixtures only.

### `fal-ai/elevenlabs/audio-isolation` (default)

Source: `https://fal.ai/models/fal-ai/elevenlabs/audio-isolation/llms.txt` and
`https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/elevenlabs/audio-isolation`, read 2026-11-02. Category
`audio-to-audio`; submit `POST https://queue.fal.run/fal-ai/elevenlabs/audio-isolation`.

| Item | What fal states | Status |
|---|---|---|
| Input `audio_url` | `string`, optional: "URL of the audio file to isolate voice from" | read |
| Input `video_url` | `string`, optional: "Video file to use for audio isolation. Either `audio_url` or `video_url` must be provided." | read; the platform sends **only** `audio_url` and refuses anything else |
| Output `audio` | `File` (`url`, ...), **required**: "The generated audio file" | read |
| Output `timestamps` | `list`, optional: "Timestamps for each word in the generated speech. Only returned if `timestamps` is set to True in the request." (no such input exists; copied from the TTS schema) | read; ignored |
| Background stem | none in the schema: one output file, the isolated voice | read; `HasBackgroundStem = false`, a background request is refused |
| Price | "**$0.1 per minutes**" | read; price row `0.10` USD per `AudioCleanupMinutes` |
| Billing unit and rounding | not stated beyond "per minutes" | **unconfirmed** |
| Max file size / duration | not stated | **unconfirmed** (the platform caps a request at `Cleanup:Hosted:MaxSourceMinutes`, default 30) |
| Licence | fal publishes no licence tag on the page; it is the ElevenLabs model hosted by fal. the owner accepts the risk (see `docs/elevenlabs-provider.md` "Exception: audio isolation via fal"). | **unconfirmed / must confirm** |

### Alternatives read (not wired)

Neither is in the catalogue and neither has a price row, so each stays **unpriced and unofferable** until a decision
adds it; their prices below are fal's, read 2026-11-02, and neither licence was read.

| Endpoint | Input fields (OpenAPI) | Output fields | Listed price | Note |
|---|---|---|---|---|
| `fal-ai/demucs` | `audio_url`, `model`, `stems`, `shifts`, `overlap`, `segment_length`, `output_format` | `vocals`, `drums`, `bass`, `guitar`, `piano`, `other` | $0.0007 per second | stem separation (open-source Demucs): voice AND a non-voice stem; licence unread |
| `fal-ai/deepfilternet3` | `audio_url`, `audio_format`, `bitrate`, `sync_mode` | `audio_file`, `timings` | $0.001 per second | speech denoise only (no background stem); licence unread |

### Billing, queue and limits

- Billing follows `UsageKind.AudioCleanupMinutes`: audio seconds measured, rounded **up** to a started minute, times the
  price-book price and the platform margin; a model with no price row is unofferable (`AudioCleanupCreditPricing`).
- Queue contract is the shared one (`FalQueueClient`: submit, poll `IN_QUEUE` / `IN_PROGRESS` / `COMPLETED`, fetch the
  `response_url`); the result file URL is downloaded through the SSRF-guarded client with no `Authorization` header.
- Typed errors: `AUDIO_VENDOR_QUOTA` (402), `AUDIO_VENDOR_RETRYABLE` (429/5xx, nothing produced), `AUDIO_VENDOR_FAILED`
  (accepted then failed, no audio, unsafe URL), `AUDIO_REQUEST_INVALID` (local schema refusal or 400/422),
  `AUDIO_PROVIDER_CONFIG` (401/403/404).

### Must measure with a real key

| # | Measurement | Why it matters |
|---|---|---|
| 1 | Output length vs input length (trimmed? padded? identical?) | the platform meters the input seconds; a drift breaks settlement |
| 2 | Output sample rate, channels, container and codec | the engine mixes the stem back; the format may force a re-encode |
| 3 | Price per minute actually billed on the invoice (rounding up? input or output length? minimum charge?) | the price row is the vendor's list sentence, not an invoice |
| 4 | File size cap and duration cap of `audio_url` (30 minutes assumed) | the request cap `MaxSourceMinutes` must sit below the vendor's |
| 5 | Voice stem vs background stem availability | the schema returns the voice only; a "keep the music" mode needs a second endpoint (Demucs) or a subtraction |
| 6 | Quality on real recordings (music under speech, room echo, two speakers, non-English) | decides default and alternatives |
| 7 | Result file retention on fal's CDN (how long the URL lives) and whether input/output are retained or trained on | `docs/provider-data-handling.md` stays `unverified` until read |
| 8 | Whether a 4xx on an unreachable or expired presigned URL is billed | the 10-minute presign lifetime is the caller's job |
| 9 | Error body shapes for 402 / 422 / quota, and whether a queue-level `COMPLETED` with `error` is billed | classification is by status code today |
| 10 | Whether the ElevenLabs terms bind a call routed through fal (licence) | the owner accepts the risk; must confirm |
| 11 | Wall-clock time per audio minute (queue wait and inference) | the poll budget is 10 minutes |

## 13. Video enhancement (upscale, frame generation)

Read 2026-11-02 from `https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=<id>` (schema) and
`https://fal.ai/models/<id>/llms.txt` (price sentence). **Nothing here was run:** no fal key exists, every client
test replays hand-written fixtures shaped like the output schemas, and the must-measure rows 36 to 41 above list
what a real key must settle. Two capabilities, both served by `FalAi` only: `VideoUpscale` (a larger
clip) and `FrameInterpolation` (more frames per second). Customer video is sent to fal as a 10-minute presigned
GET; audio is never part of either capability (the caller remuxes the untouched source audio locally). Requests
are metered at vendor cost times the platform markup through the price book, 120 s of source per
request (`VideoEnhance:Hosted:MaxSourceSeconds`).

### Chosen defaults and what we send

| Capability | Default | Alternative | Fields sent |
|---|---|---|---|
| `VideoUpscale` | `fal-ai/topaz/upscale/video` | `fal-ai/bytedance-upscaler/upscale/video` | Topaz: `video_url`, `upscale_factor` (1..4, computed by the caller), `model: "Proteus"`, `H264_output: true`. **Never `target_fps`** (it turns interpolation on and changes frame timing). bytedance: `video_url`, `scale_ratio` (1.1..4), `enhancement_tier: "standard"` (pro is 10x the price), and `target_fps` = the source rate rounded and clamped to 24..120 (bytedance forces a rate; the local conform restores the exact fps). |
| `FrameInterpolation` | `fal-ai/rife/video` | `fal-ai/film/video` | `video_url`, `num_frames` (the count of IN-BETWEEN frames: 1 for 2x, 3 for 4x), `use_calculated_fps: true`, `use_scene_detection: true`, `loop: false`; FILM adds `enable_safety_checker: true`. **Never `sync_mode: true`.** Hosted multipliers offered: 2x and 4x only (8x is local). |

The client refuses a request body that carries a field not in the endpoint's allowed set
(`VideoEnhanceModelCatalog`), before anything is submitted. Not chosen (priced per megapixel-frame):
`fal-ai/seedvr/upscale/video` ($0.001 per megapixel), `fal-ai/flashvsr/upscale/video` ($0.0005 per megapixel),
`fal-ai/video-upscaler` ($0.0008 per megapixel).

### Cited prices (the vendor's sentences, verbatim)

- `fal-ai/topaz/upscale/video`: "For every second a video your request will cost $0.01 for up to 720p, $0.02 for
  720p to 1080p, and $0.08 for above 1080p output. Price doubles for 60fps output. For Gaia 2 output costs half of
  the prices."
- `fal-ai/bytedance-upscaler/upscale/video`: "Upscaling to 1080p costs $0.0072/s, 2K costs $0.0144/s, and 4K costs
  $0.0288/s, at a 30fps rate. Processing at 60fps doubles the cost for any resolution. Processing in pro mode makes
  the price 10 times."
- `fal-ai/rife/video` and `fal-ai/film/video`: "Your request will cost $0.0013 per compute second."

### Why the units differ

- **Upscale: `UsageKind.VideoEnhanceSeconds`** = billed seconds of OUTPUT video, rounded up to whole seconds, priced
  per output tier by one `code#tier` price card each: `720p` (short side up to 720), `1080p` (721 to 1080), `uhd`
  (above 1080), and the `-hfr` twins (exactly double) when the output frame rate is above 30 (the vendor says
  "60fps"; anything above 30 is priced doubled, the safe direction). Topaz has all six cards; bytedance has the
  standard-tier `1080p`, `uhd`, `1080p-hfr`, `uhd-hfr` (its 2K output is priced as 4K, and it has no 720p card, so a
  720p bytedance output is **unpriced and unofferable**). Bytedance's output rate is the `target_fps` we send.
- **Interpolation: `UsageKind.VideoEnhanceComputeSeconds`** = vendor GPU compute seconds. The result body carries no
  compute time, so the platform HOLDS `ceil(outputFrames x ComputeSecondsPerOutputFrame x (outputPixels /
  1920x1080))` (`VideoEnhance:Hosted:ComputeSecondsPerOutputFrame`, default **0.05, UNMEASURED**) and settles at the
  hold (the existing rule "floored at the hold when metering failed"). Until row 37 is measured, an interpolation
  charge is an estimate and says so.
- An endpoint or tier with no cited price has no row and stays unofferable (fail closed, never a default price).

### 13b. Extend past the border (`VideoOutpaint`)

The platform appended a third hosted capability, `VideoOutpaint`, served by `FalAi` only: the same clip on a larger
canvas with generated margins. It is its own capability, not a mode of `VideoUpscale` (the vendor fields and price
sentences differ). Read 2026-11-02 from the same OpenAPI and `llms.txt` URLs; **nothing here was run**.

| Capability | Default | Alternative | Fields sent |
|---|---|---|---|
| `VideoOutpaint` | `fal-ai/wan-vace-14b/outpainting` | `fal-ai/luma-dream-machine/ray-2/reframe` | Wan: `video_url`, `prompt` (required), `expand_left/right/top/bottom` (a side with no margin is `false`), `expand_ratio` (the largest margin, 0..1), `match_input_num_frames: true`, `match_input_frames_per_second: true`, `resolution` (`480p`, `580p` or `720p` from the canvas short side), **`enable_safety_checker: true`** (the endpoint defaults it to false). Luma: `video_url`, `aspect_ratio`, `prompt`; no per-side margins and no `4:5`. |

- **Prices (verbatim):** Wan VACE: "$0.08 per video second for 720p, $0.06 per video second for 580p, $0.04 per video
  second for 480p. Video seconds are calculated at 16 frames per second." Luma reframe: "$0.2 per seconds". Wan bills
  `ceil(frames / 16)` video seconds whatever the source rate; Luma bills the clip's seconds. Both use
  `UsageKind.VideoEnhanceSeconds`; Wan has three price cards `code#480p`, `#580p`, `#720p`, Luma one flat card.
- **Frame floor:** Wan needs at least 81 frames, so a source under `81 / fps` seconds is refused before upload with
  `ENHANCE_SOURCE_TOO_SHORT` (route and estimate: 422 `SOURCE_TOO_SHORT` with `minSeconds`), refunded. As a second
  guard the settlement, estimate and hold all bill `max(frames, 81)` at 16 fps, so no path can under-bill a short clip.
- **Frame ceiling:** Wan takes at most 241 frames, so a source over `241 / fps` seconds is refused with
  `ENHANCE_SOURCE_TOO_LONG` and the cap in seconds, before anything is uploaded. Luma has no cap beyond the 120 s one.
- **Canvas:** the step takes a `targetAspect` (`16:9`, `9:16`, `1:1`, `4:5`) XOR "sides + one amount": four margins, each at
  most 0.5 of a side, of which every one that grows is the SAME value (Wan has ONE `expand_ratio` for all enabled sides, so
  uneven margins would ask the vendor for a different frame than the one priced; they are refused with a 400). The
  delivered file is conformed to exactly the canvas size: a vendor picture of another shape is scaled to fit and padded with
  black, centred. Luma also receives `enable_safety_checker: true` (declared by its schema). The platform computes the even-pixel canvas locally (`VideoOutpaintGeometry`) and refuses what cannot be
  expressed (a portrait clip cannot become 16:9 inside half a side; the picture already having the shape is a no-op):
  never a clamp. The result is conformed to that canvas at the source's own frame rate and duration, with the original
  audio remuxed.
- **Prompt:** optional, at most 500 characters, control characters stripped, screened by the moderation gate before
  anything is reserved or uploaded (a block is `CONTENT_BLOCKED`); a neutral default is sent when blank because the
  model requires one. The default is platform text and is not screened as customer text.
- **Must measure (needs a key):** seams and flicker at the original/generated border; what the vendor does between
  81 and 241 frames and at the 17 to 80 frame floor; whether audio is kept or dropped (the platform always remuxes the
  source audio and ignores the vendor's); that `resolution` is honoured above 720p sources.

### Jobs outlive one poll

Both clients split submit and poll like `VideoGenerate`: `SubmitAsync` returns a handle (`request_id`, `status_url`,
`response_url`) the caller persists in the `external_generation_jobs` ledger; `PollAsync` is bounded by
`VideoEnhance:Hosted:PollTimeoutMinutes` (default 30) and raises `ENHANCE_STILL_RUNNING` when the budget runs out
while the job may still finish; `ResumeAsync` reads a handle loaded after a restart and never submits. Typed errors:
`ENHANCE_VENDOR_QUOTA` (402), `ENHANCE_VENDOR_RETRYABLE` (429 at submit only: refused before queueing),
`ENHANCE_SUBMIT_OUTCOME_UNKNOWN` (5xx, 408 or a transport error at submit: the vendor may have queued and will bill the job, so it is **never resubmitted automatically**, mirroring VideoGenerate's `SUBMIT_OUTCOME_UNKNOWN`), `ENHANCE_VENDOR_FAILED`,
`ENHANCE_REQUEST_INVALID`, `ENHANCE_VENDOR_REFUSED` (safety checker / nsfw), `ENHANCE_PROVIDER_CONFIG` (401/403/404).
A transient poll error (transport failure, 5xx, 408/429, garbled body) is retried with bounded backoff inside the poll budget and, if it outlasts it, ends as `ENHANCE_STILL_RUNNING` with the handle intact; a 4xx on poll is permanent. The poll budget and the source cap are bound from `VideoEnhance:Hosted:PollTimeoutMinutes` and `VideoEnhance:Hosted:MaxSourceSeconds` by `VideoEnhanceClientFactory`. Bytedance fails closed (unpriced, unofferable) above a 4K output short side (2160) and above 60 fps, the limits its cited sentence covers. A download is accepted only with a `video/*` Content-Type (no missing type, no octet-stream).
The enhanced clip is downloaded through the SSRF guard (`GeneratedClipUrlGuard`), `video/*` or octet-stream only,
512 MiB maximum, no Authorization header.

### Must measure (summary; the full rows are 36 to 41 in the table at the top)

| Question | Assumed today |
|---|---|
| Audio kept or dropped by each endpoint | Dropped or ignored: the source audio is remuxed locally |
| Maximum input length and size | 120 s per request |
| Compute seconds per output frame (RIFE/FILM) | 0.05 at 1080p (unmeasured) |
| Output frame rate exactness (RIFE calculated fps, bytedance `target_fps`) | Within tolerance; the local conform restores the exact rate |
| Whether 50 fps bills as 60 (Topaz) | Yes (priced doubled) |
| Quality on fast motion (4x, scene cuts) | Unknown |

## 14. Talking heads and avatar stills (avatars phase 3, added 2026-11-06)

The still-then-animate path of [`avatars.md`](avatars.md#the-still-then-animate-path-phase-3): the engine makes ONE still of
an avatar from its reference pictures with an instruction-edit endpoint, then a talking-head endpoint animates that still
so the person says the words of a speech track (a `Voiceover` step of the run). Job `FalVideoJob.TalkingHead` in
`FalAiVideoCatalog`; declared through `IVideoReferenceInputSupport` as `AnimatesStill` (one picture, one audio, audio
drives speech). The talking-head model is registered under its own capability, `LipSync` (fal.ai only; the model code must be one of the two
endpoints below), not under `VideoGeneration`: a `VideoGeneration` row naming one is refused, and the price rows are the same
per-resolution ones. **Nothing below was run with a key**: the OpenAPI documents and `llms.txt` pages were READ on 2026-11-06.

**Why these two.** The design's shortlist (`docs/design/avatars.md` "Vendor facts") read on 2026-11-06:

| Endpoint | Required inputs (schema) | Stated bounds | Price (llms.txt) | Verdict |
|---|---|---|---|---|
| `minimax/h3-max/lip-sync/image-to-video` | `image_url`, `audio_url` | audio "at least 5 seconds ... up to 15 minutes"; image aspect 0.4..2.5; `resolution` enum | $0.05/s 480p, $0.08 768p, $0.16 1080p, $0.32 2K; x1.2 over 15 s | **default**: the only route that states its input bounds, cheapest published tier |
| `fal-ai/kling-video/ai-avatar/v2/standard` | `image_url`, `audio_url` (`prompt` optional, default ".") | none stated | $0.0562/s flat | **alternative**: one exact flat price, a motion prompt |
| `veed/fabric-1.0` | `image_url`, `audio_url`, `resolution` | URLs `maxLength` 2083 | $0.08/s 480p, $0.15 720p (design doc) | not wired: a 2083-character URL cap rules out the data URIs this platform sends |
| `fal-ai/creatify/aurora` | `image_url`, `audio_url` | none stated | $0.07/s 480p, $0.14 720p (design doc) | not wired: dearer than both |
| `fal-ai/bytedance/omnihuman/v1.5` | image + audio | - | $0.16/s (design doc) | not wired: the dearest |

**The still** is made with `google/nano-banana-2.1/edit` (section 6 has its schema; already the editor's instruction-edit
default and priced in `ModelPriceCatalog.FalImages`): `image_urls` = the avatar's first chosen picture then up to two
more (the platform's cap of three; the schema states no maximum), `prompt` = a fixed first-party instruction wrapping the
step's prompt, `aspect_ratio` = the step's ratio, `resolution` left at the 1K default. Any other instruction-edit
endpoint that takes an image ARRAY may be the row's model; a single-image endpoint is refused. Only a fal.ai `ImageEdit`
row is used for a still (the data URIs and the retention header below are fal's).

**Retention.** Every avatar request (the still's edit and the talking head's submit) carries `X-Fal-Store-IO: 0`
(`VideoGenerationRequest.NoVendorRetention`, `ImageEditRequest.NoVendorRetention`); an ordinary request is unchanged
(row 27). The pictures and the speech travel as base64 data URIs.

### `minimax/h3-max/lip-sync/image-to-video`

- **Status: schema read, never called** (no key; every behaviour below is the vendor's statement).
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=minimax/h3-max/lip-sync/image-to-video>  (input schema `H3MaxLipSyncImageToVideoInput`, output schema `H3MaxLipSyncImageToVideoOutput`), read 2026-11-06
- Used by the platform as: the **default** talking-head endpoint (<https://fal.ai/models/minimax/h3-max/lip-sync/image-to-video>).

**INPUT** (`H3MaxLipSyncImageToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `audio_url` | string | yes | - | - | Audio to synchronize the mouth movements to, at least 5 seconds long. Supports up to 15 minutes. The output video matches the supplied audio duration. |
| `resolution` | string | no | "768P" | "480P", "768P", "1080P", "2K" | Output resolution. Uses the supported aspect ratio nearest the image. |
| `enable_transcription` | boolean | no | true | - | Transcribe the supplied audio to guide lip synchronization. Turn this off if the audio has a lot of non-word sound, such as music, singing, laughter or noise, which the transcript may turn into words that were never spoken. |
| `seed` | integer | null min=0 max=2147483647 | no | - | - | Random seed; selected randomly if omitted. |
| `image_url` | string | yes | - | - | Image to animate. The aspect ratio must be between 0.4 and 2.5. |
| `enable_safety_checker` | boolean | no | true | - | Enable content safety checks. |

**OUTPUT** (`H3MaxLipSyncImageToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | Lip-synced video with the supplied soundtrack. |
| `duration` | number | yes | - | - | Output duration in seconds. |
| `timings` | object | null | no | - | - | Timing breakdown in seconds. 'inference' is the DiT denoising time on the GPU backend, summed over every window this request generated. Null on routes that do not report backend timings. |
| `seed` | integer | yes | - | - | Random seed used for generation. |

**Wired by the platform** (`FalAiVideoCatalog`, job `TalkingHead`): still -> `image_url` (required, the avatar still the engine made); speech -> `audio_url` (required, one track, 5..15 s, the platform's ceiling: above 15 s the vendor adds a 1.2x multiplier); prompt -> NO FIELD (none is sent); duration -> NO FIELD (the clip is as long as the audio; the take is reserved and billed at the audio's whole seconds); resolution -> `resolution` ("480P", "768P", "1080P", "2K"); ratio -> NO FIELD (the canvas follows the still, made at the step's ratio). `enable_transcription`, `seed` and `enable_safety_checker` are not forwarded (schema defaults stand).

- Price sentence (verbatim): Billing is calculated per second of generated video. Video costs **$0.05** per second at **480p**, **$0.08** per second at **768p**, **$0.16** per second at **1080p**, and **$0.32** per second at **2K**. A 1.2x multiplier is added to videos over 15 seconds. A **5-second** video at **768p** costs **$0.40**. A **30-second** video at **768p** costs **$2.88**.
  - <https://fal.ai/models/minimax/h3-max/lip-sync/image-to-video> (the same sentence is rendered by <https://fal.ai/models/minimax/h3-max/lip-sync/image-to-video/llms.txt>)

### `fal-ai/kling-video/ai-avatar/v2/standard`

- **Status: schema read, never called** (no key; every behaviour below is the vendor's statement).
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/kling-video/ai-avatar/v2/standard>  (input schema `KlingVideoAiAvatarV2StandardInput`, output schema `KlingVideoAiAvatarV2StandardOutput`), read 2026-11-06
- Used by the platform as: the **alternative** talking-head endpoint (<https://fal.ai/models/fal-ai/kling-video/ai-avatar/v2/standard>).

**INPUT** (`KlingVideoAiAvatarV2StandardInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `image_url` | string | yes | - | - | The URL of the image to use as your avatar |
| `enable_safety_checker` | boolean | no | true | - | Disabling safety checks requires account authorization; unauthorized requests are always checked. |
| `prompt` | string | no | "." | - | The prompt to use for the video generation. |
| `audio_url` | string | yes | - | - | The URL of the audio file. |

**OUTPUT** (`KlingVideoAiAvatarV2StandardOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video |
| `duration` | number | yes | - | - | Duration of the output video in seconds. |

**Wired by the platform** (`FalAiVideoCatalog`, job `TalkingHead`): still -> `image_url` (required); speech -> `audio_url` (required, one track, 5..15 s: the schema states no bounds, so the default's are used until measured); prompt -> `prompt` (optional; the step's prompt, at most 2500 characters, the platform's cap); duration -> NO FIELD; resolution -> NO FIELD (one flat price, token `native`); ratio -> NO FIELD (the canvas follows the still). `enable_safety_checker` is not forwarded.

- Price sentence (verbatim): **Price**: $0.0562 per seconds
  - <https://fal.ai/models/fal-ai/kling-video/ai-avatar/v2/standard> (the same sentence is rendered by <https://fal.ai/models/fal-ai/kling-video/ai-avatar/v2/standard/llms.txt>)

### Must measure with a real key (talking heads and stills)

| # | Measurement | What the platform assumes today |
|---|---|---|
| T1 | Do both talking-head routes accept a base64 **data URI** in `image_url` and `audio_url`? | Yes (fal-cdn's platform-wide statement); a refusal surfaces as `PROVIDER_REJECTED` |
| T2 | Is `X-Fal-Store-IO: 0` honoured on the talking-head and edit routes (no entry under Recent History)? | Yes |
| T3 | Kling avatar's real audio floor and ceiling, and its output resolution | 5..15 s, as the default; resolution `native` |
| T4 | H3 lip sync: is the clip exactly the audio's length, and is a 5.0 s track billed as 5 s? | Billed at the audio's whole seconds, rounded up |
| T5 | Real latency per take (queue + inference) | Inside the step's `TimeoutSeconds` (template 1200 s) |
| T6 | Identity hold: does the still look like the person, and does the talking head keep that face? Two consenting subjects, ten prompts, scored by people | Not measured: the reason for a live smoke run before the plan feature is shown |
| T7 | `nano-banana-2.1/edit` with three reference pictures: real cost (token-priced) and whether it keeps the face | Held at the 2K card ($0.063), metered at the 1K tier |
| T8 | Faces the vendors refuse (well-known people, minors) and their error text | Surfaces as `PROVIDER_REJECTED` / `AVATAR_STILL_FAILED` |
| T9 | Licence: both routes are fal "commercial" partner models; the vendor's own terms for likeness were not read | fal's commercial terms; our consent is the only consent (design "Legal anchors") |

## 15. Reference-to-video: avatars and objects as reference pictures (added 2026-11-16)

The `References` path of [`design/avatar-objects.md`](design/avatar-objects.md) "Subjects in a video generation step": a
`VideoGenerate` step that shows one to three project avatars (people and objects) on a model with its own reference
mode sends each subject's PICTURES as reference items. Job `FalVideoJob.ReferenceToVideo` in `FalAiVideoCatalog`;
declared through `IVideoReferenceInputSupport` (pictures only, never with a frame, `AudioDrivesSpeech` false). Every
other fal video endpoint shows subjects through ONE composed first frame instead (the `FirstFrame` path: the still is made
by the instruction-edit endpoint of section 14, recipe `subject-scene-still-v1`, up to three pictures of each subject and
six in all), and a text-to-video endpoint is refused with the name of its image-capable sibling
(`VideoSubjectSiblings`). **Nothing below was run with a key**: the OpenAPI documents and the model pages were READ on
2026-11-16.

**Which endpoints, and why not the others.** Eight reference-to-video endpoints were read. The rule is section 10's:
an endpoint is catalogued only when its schema is read AND its price can be stated per second of video without a guess,
or with a surcharge the platform can bound and hold.

| Endpoint | Reference inputs (schema) | Price (model page, 2026-11-16) | Verdict |
|---|---|---|---|
| `alibaba/wan-3.0/reference-to-video` | `reference_image_urls` (maxItems 10), `reference_video_urls` (5, 15 s, at least 16 fps), `reference_audio_urls` (5, 15 s) | $0.05/s 480p, $0.10 720p, $0.20 1080p; input VIDEO duration billed on top | **default**: the only one that states its limits and its price; pictures only are sent |
| `fal-ai/veo3.1/reference-to-video` | `image_urls` (required, no maxItems) | $0.20/s without audio, $0.40 with, at 720p or 1080p; $0.40 / $0.60 at 4k | **alternative**: one exact price per tier; at most 3 pictures and only `"8s"` are sent |
| `fal-ai/veo3.1/fast/reference-to-video` | `image_urls` (required, no maxItems) | $0.10/s audio off, $0.15 audio on (no resolution named) | **alternative**: 720p and 1080p only (the page states no 4k price) |
| `fal-ai/vidu/q4/reference-to-video` | `reference_image_urls` (up to 12, data URIs named), `reference_audio_urls` (3) | "$0.0315 at 540p, $0.0665 at 720p, $0.084 at 1080p, $0.133 at 2K, and $0.273 at 4K"; billing metadata: unit "units" at $0.0035 | not catalogued: the figures are exactly the image-to-video route's promotional rates (30% off until 2026-11-30, section 10) and this page names neither the promotion nor the rate after it, so the card after 2026-11-30 is not stated |
| `minimax/h3-max/reference-to-video` | `reference_image_urls` (9), `reference_video_urls` (3), `reference_audio_urls` (3) | $0.05/s 480p, $0.08 768p, $0.16 1080p, plus reference tokens above 4,096 at $0.02 per 1,000 (a picture is 1,024 to 2,560 tokens by its shape; a frame counts too) | not catalogued: the surcharge is prorated by each picture's shape, which a per-second card plus a per-picture figure cannot state exactly; MiniMax direct serves the same family with a published per-picture price |
| `minimax/h3-max-turbo/reference-to-video` | as above | $0.05/s 480p, $0.08 768p, $0.16 1080p (5 s minimum), plus the same reference tokens, plus generated reference pictures at "about $0.02 to $0.04 each" | not catalogued: the same surcharge, and an "about" figure |
| `fal-ai/kling-video/o3/standard/reference-to-video` | `image_urls`, `elements` (frontal picture plus references) | the page carries five different per-second sentences ($0.084 / $0.112, $0.42, $0.112 / $0.14, $0.168, $0.126) and its billing metadata says $0.14 | not catalogued: no single figure can be attributed to this endpoint |
| `fal-ai/kling-video/o3/pro/reference-to-video` | as the standard tier | as the standard tier's page | not catalogued: the same |

**What is sent.** The pictures only, as base64 data URIs, in the endpoint's own array field, in the step's subject order
(subject by subject, each in its own order). The slots are shared in turns: every subject gets one picture before any
subject gets a second (`VideoReferenceInput.SelectShared`), inside the endpoint's ceiling and the platform's body bound
(`FalAiVideoGenerationClient.MaxReferenceRequestBytes`, 24 MiB once encoded; fal documents no ceiling). What does not fit
is listed per subject in `meta.subjects[].referencesSkipped`. A subject left with no picture refuses the step before
anything is reserved. Reference videos and reference audio are never sent on fal: Wan bills input video duration on top
and requires at least 16 fps, a frame rate the platform does not store, and no page states a price for reference audio.

**Retention.** A request that carries reference pictures is submitted with `X-Fal-Store-IO: 0`, whether a person is among
the subjects or not (`FalAiVideoGenerationClient.CarriesPersonalMedia`). On the `FirstFrame` path the still's edit always
carries it, and the video submit carries it when a person is among the subjects.

### `alibaba/wan-3.0/reference-to-video`

- **Status: schema read, never called** (no key; every behaviour below is the vendor's statement).
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=alibaba/wan-3.0/reference-to-video>  (input schema `Wan30ReferenceToVideoInput`, output schema `Wan30ReferenceToVideoOutput`), read 2026-11-16
- Used by the platform as: the **default** reference-to-video endpoint (<https://fal.ai/models/alibaba/wan-3.0/reference-to-video>).

**INPUT** (`Wan30ReferenceToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `reference_audio_urls` | array of string maxItems=5 | no | - | - | Up to 5 reference audio URLs totaling at most 15 seconds. |
| `duration` | integer minimum=2 maximum=30 | null | no | 5 | - | Output duration in seconds. Set to null for smart duration, which lets the model pick a length from the prompt and reference media. |
| `web_url` | string | null | no | - | - | Public webpage URL to base the video on. Requires enable_thinking=true. Only pages that do not require login can be read. |
| `enable_prompt_expansion` | boolean | no | true | - | Enable intelligent prompt rewriting. Disabling it can save roughly 20-60 seconds of latency but is likely to degrade generation quality. |
| `reference_image_urls` | array of string maxItems=10 | no | - | - | Up to 10 reference image URLs. |
| `seed` | integer minimum=0 maximum=2147483647 | null | no | - | - |  |
| `resolution` | string | no | "1080p" | "480p", "720p", "1080p" | Output video resolution tier. |
| `reference_video_urls` | array of string maxItems=5 | no | - | - | Up to 5 reference video URLs totaling at most 15 seconds. Each clip must be at least 16 fps. Input video duration is billed in addition to output duration. |
| `prompt` | string maxLength=20000 | null | no | - | - | Text prompt directing how the reference media is used. Reference media can be addressed positionally, e.g. 'the subject in Image 1 walks past Video 1'. |
| `enable_safety_checker` | boolean | no | true | - | Enable content moderation for input and output. Disabling it requires account authorization; unauthorized requests are always checked. |
| `file_url` | string | null | no | - | - | Document URL to base the video on. Requires enable_thinking=true. |
| `aspect_ratio` | string | no | "adaptive" | "adaptive", "16:9", "4:3", "1:1", "3:4", "9:16" | Output aspect ratio, or adaptive selection. |
| `audio` | boolean | no | true | - | Include generated audio. |
| `enable_thinking` | boolean | no | false | - | Enable enhanced reasoning before generation. |

**OUTPUT** (`Wan30ReferenceToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `duration` | number | yes | - | - | Generated video duration in seconds. |
| `video` | ? | yes | - | - | The generated video file. |
| `seed` | integer | yes | - | - | The seed used for generation. |
| `actual_prompt` | string | null | no | - | - |  |

**Wired by the platform** (`FalAiVideoCatalog`, job `ReferenceToVideo`): reference pictures -> `reference_image_urls` (1 to 10; the schema marks nothing required, the platform requires at least one picture and a prompt); prompt -> `prompt` (required by the platform, max 20000); duration -> a JSON integer, 2..30 (never `null`); resolution -> `resolution` ("480p", "720p", "1080p"); ratio -> `aspect_ratio`; frames -> NO FIELD (a frame is refused). `reference_video_urls`, `reference_audio_urls`, `file_url`, `web_url`, `enable_thinking`, `enable_prompt_expansion`, `audio`, `seed` and `enable_safety_checker` are not forwarded (schema defaults stand).

- Price sentence (verbatim): For every second of video you generate, you will be charged **$0.05** **480p**, **$0.10** **720p**, or **$0.20** **1080p**. For example, a **5s** video at **720p** will cost **$0.50**. **Pricing is subject to change.**
  - <https://fal.ai/models/alibaba/wan-3.0/reference-to-video> (billing metadata on the same page: unit "seconds", price 0.05)
  - The schema adds, on `reference_video_urls`: "Input video duration is billed in addition to output duration." The platform sends no reference video, so the per-second figures are the whole price.

### `fal-ai/veo3.1/reference-to-video`

- **Status: schema read, never called** (no key; every behaviour below is the vendor's statement).
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1/reference-to-video>  (input schema `Veo31ReferenceToVideoInput`, output schema `Veo31ReferenceToVideoOutput`), read 2026-11-16
- Used by the platform as: an **alternative** reference-to-video endpoint (<https://fal.ai/models/fal-ai/veo3.1/reference-to-video>).

**INPUT** (`Veo31ReferenceToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `safety_tolerance` | string | no | "4" | "1", "2", "3", "4", "5", "6" | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `image_urls` | array of string | yes | - | - | URLs of the reference images to use for consistent subject appearance |
| `generate_audio` | boolean | no | true | - | Whether to generate audio for the video. |
| `resolution` | string | no | "720p" | "720p", "1080p", "4k" | The resolution of the generated video. |
| `duration` | string | no | "8s" | - | The duration of the generated video. |
| `auto_fix` | boolean | no | false | - | Whether to automatically attempt to fix prompts that fail content policy or other validation checks by rewriting them. |
| `aspect_ratio` | string | no | "16:9" | "16:9", "9:16" | The aspect ratio of the generated video. |
| `prompt` | string maxLength=20000 | yes | - | - | The text prompt describing the video you want to generate |

**OUTPUT** (`Veo31ReferenceToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video. |

**Wired by the platform** (`FalAiVideoCatalog`, job `ReferenceToVideo`): reference pictures -> `image_urls` (required; 1 to 3: the schema states no maximum, so the platform sends at most the three of the vendor's own example request); prompt -> `prompt` (required, max 20000); duration -> the JSON string `"8s"` only (the schema's default; it lists no other value); resolution -> `resolution` ("720p", "1080p", "4k"); ratio -> `aspect_ratio` ("16:9", "9:16"); frames -> NO FIELD. `generate_audio` (default true), `auto_fix` and `safety_tolerance` are not forwarded, so the WITH-AUDIO figure is the card.

- Price sentence (verbatim): For every second of video you generate you will be charged $0.20 without audio or $0.40 with audio for 720p or 1080p. At 4k, you will be charged $0.40 per second without audio, or $0.60 with audio. For example, 8 second video at 1080p with audio on will cost $3.20
  - <https://fal.ai/models/fal-ai/veo3.1/reference-to-video> (billing metadata on the same page: unit "seconds", price 0.4)

### `fal-ai/veo3.1/fast/reference-to-video`

- **Status: schema read, never called** (no key; every behaviour below is the vendor's statement).
- Schema source: <https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/veo3.1/fast/reference-to-video>  (input schema `Veo31FastReferenceToVideoInput`, output schema `Veo31FastReferenceToVideoOutput`), read 2026-11-16
- Used by the platform as: an **alternative** reference-to-video endpoint (<https://fal.ai/models/fal-ai/veo3.1/fast/reference-to-video>).

**INPUT** (`Veo31FastReferenceToVideoInput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `safety_tolerance` | string | no | "4" | "1", "2", "3", "4", "5", "6" | The safety tolerance level for content moderation. 1 is the most strict (blocks most content), 6 is the least strict. |
| `image_urls` | array of string | yes | - | - | URLs of the reference images to use for consistent subject appearance |
| `generate_audio` | boolean | no | true | - | Whether to generate audio for the video. |
| `resolution` | string | no | "720p" | "720p", "1080p", "4k" | The resolution of the generated video. |
| `duration` | string | no | "8s" | - | The duration of the generated video. |
| `auto_fix` | boolean | no | false | - | Whether to automatically attempt to fix prompts that fail content policy or other validation checks by rewriting them. |
| `aspect_ratio` | string | no | "16:9" | "16:9", "9:16" | The aspect ratio of the generated video. |
| `prompt` | string maxLength=20000 | yes | - | - | The text prompt describing the video you want to generate |

**OUTPUT** (`Veo31FastReferenceToVideoOutput`)

| property | type | required | default | enum | description (verbatim, whitespace collapsed) |
|---|---|---|---|---|---|
| `video` | ? | yes | - | - | The generated video. |

**Wired by the platform** (`FalAiVideoCatalog`, job `ReferenceToVideo`): reference pictures -> `image_urls` (required; 1 to 3, the platform's ceiling); prompt -> `prompt` (required, max 20000); duration -> the JSON string `"8s"` only; resolution -> `resolution` ("720p", "1080p": the schema's "4k" is NOT offered, because the page states no 4k price and every other veo route charges more at 4k); ratio -> `aspect_ratio` ("16:9", "9:16"); frames -> NO FIELD. `generate_audio` (default true), `auto_fix` and `safety_tolerance` are not forwarded, so the WITH-AUDIO figure is the card.

- Price sentence (verbatim): For every second of video you generated, you will be charged **$0.10** (audio off) or **$0.15** (audio on). For example, a **5s video** with audio on will cost **$0.75**.
  - <https://fal.ai/models/fal-ai/veo3.1/fast/reference-to-video> (billing metadata on the same page: unit "seconds", price 0.15)

### Must measure with a real key (reference-to-video and scene stills)

| # | Measurement | What the platform assumes today |
|---|---|---|
| R1 | Do the three reference routes accept a base64 **data URI** in every item of `reference_image_urls` / `image_urls`? | Yes (fal-cdn's platform-wide statement); a refusal surfaces as `PROVIDER_REJECTED` |
| R2 | The real request-body ceiling of the queue for inline pictures | The platform keeps the encoded pictures under 24 MiB (`MaxReferenceRequestBytes`); what does not fit is left out and reported |
| R3 | Veo 3.1 reference routes: the real maximum of `image_urls` and the durations accepted besides `"8s"` | 3 pictures and `"8s"` only |
| R4 | Veo 3.1 Fast reference route: the 1080p and 4k prices (the page states one pair of figures and names no resolution) | 720p and 1080p at $0.15/s with audio; 4k not offered |
| R5 | Wan 3.0 reference route: is a picture-only request billed exactly the output seconds, and is `audio: true` (the default, not forwarded) free? | Yes: the per-second card is the whole price |
| R6 | Is `X-Fal-Store-IO: 0` honoured on the three reference routes? | Yes |
| R7 | Subject hold: with pictures of two or three subjects in one array, does each model keep each subject apart (a person's face, an object's shape)? Scored by people | Not measured: the pictures go subject by subject and the prompt is the step's own |
| R8 | Scene still (`subject-scene-still-v1`) with up to six input pictures on `google/nano-banana-2.1/edit`: the real cost (token-priced) and whether every subject is kept | Held at the 2K card ($0.063) per still, metered at the 1K tier, as the talking head's still (T7) |
| R9 | When the video model's ratio word is not an aspect ratio (`native`, `adaptive`, `auto`), the still is made at the picture model's own shape: what canvas does the clip get? | The canvas follows the first frame; no ratio is sent to the picture model |
| R10 | Vidu Q4 reference route after 2026-11-30: its per-second prices | Not catalogued until the page states them |

## Cross-cutting facts that affect the integration

Read on 2026-10-10; paraphrased except where quoted.

- **Concurrency and failed requests** (<https://docs.fal.ai/faq>): the FAQ says every account has a concurrency limit, new accounts start at 2 concurrent requests, rising automatically with credit purchases up to 40; server errors (HTTP 500+) are never charged, while client errors such as invalid inputs (HTTP 422) may still be charged if a runner already spent GPU time.
- **Media lifetime** (<https://docs.fal.ai/docs/documentation/model-apis/media-expiration>; the FAQ also says generated files are available "for at least 7 days by default"): the request header `X-Fal-Object-Lifecycle-Preference` takes JSON with `expiration_duration_seconds` (a number, or `null` for no expiration) and an `initial_acl` field. Expired files are permanently deleted. Input uploads and output media are subject to the same controls. Download results before they expire.
- **Payload storage** (same page): request inputs/outputs (the JSON) are stored for 30 days by default; send header `X-Fal-Store-IO: 0` to prevent that storage (CDN files are unaffected); a Platform API "Delete Request Payloads" call deletes payloads and CDN outputs of a completed request (input CDN files are not deleted).
- **Do not expose fal to end users** (Terms of Service, API materials licence): the Terms say "Client will not expose any of the Services APIs directly to any End Users." ReelBolt's server-side proxy design is consistent with this.
- **Partner endpoints:** the embedded billing metadata flags some endpoints `is_partner_api: true` (for example `fal-ai/flux-2-pro`, `fal-ai/bytedance/seedream/v4.5/edit`); the sources read do not explain what that implies beyond the flag.

## Recommendations

### (a) Minimal set for v1 of the editor (one per job)

**Superseded by the owner directive of 2026-10-10 (section 9) wherever a newer family exists:** instruction edit now defaults to `google/nano-banana-2.1/edit`, text-to-image to `ideogram/v4.5`, and the catalogue in `FalAiImageClient.Catalog` is the authority. The table below is the original v1 survey.

Chosen only from what the schemas and prices above establish. "Measure" means: run a controlled test before shipping because the schema is silent.

| Job | Pick | Why (grounded in the sections above) | Measure first |
|---|---|---|---|
| Subject selection / cut-out | Keep what is integrated (`sam-3-1/image`, `evf-sam`, `birefnet/v2`) | Nothing found beats them on a stated advantage; `fal-ai/sam-3/image` costs $0.005 vs $0.01 but shares the unstated coordinate space. | SAM 3 input coordinate space if switching |
| Remove an object with a user-painted mask | `fal-ai/object-removal/mask` (primary), `fal-ai/flux-pro/v1/erase` (higher-end alt) | Both STATE white = remove, are mask-based, and are priced openly ($0.006-$0.024 by tier; $0.03 first MP + $0.004/MP). `fal-ai/lama` (integrated) shows "$0 per compute second", i.e. unknown cost. | Quality tiers on a thumbnail-size image; whether `mask_expansion` 15 default over-erodes small subjects |
| Fill a hole with generated content (after moving a subject) | `fal-ai/flux-pro/v1/fill` ($0.05 per megapixel) | Prompted fill with required equal-size mask/image; also usable for outpaint. | **Mask polarity (NOT STATED)**; as an alternative with STATED polarity use `fal-ai/inpaint` (white = inpaint) |
| Replace the background | Generate the new background (job 7), composite locally with the existing cut-out; for a one-call path `fal-ai/bria/background/replace` ($0.04 per generation) | Local compositing is free and deterministic; the one-call endpoint accepts a prompt or reference image but not a mask. | Edge quality on hair; whether a transparent PNG input is accepted |
| Relight | `fal-ai/image-apps-v2/relighting` ($0.04 per image, 18 named styles) with `fal-ai/iclight-v2` ($0.1 per megapixel, free-text prompt + optional mask) as the controllable option | Enum-driven UI is possible only for the former. | Real output resolution of image-apps-v2 (only "4K" is mentioned); iclight mask polarity |
| Outpaint / reframe | `fal-ai/bria/expand` ($0.04 per generation) | Takes the finished canvas plus the original's size and position (array order resolved from fal's schema example and Bria's reference), so "fit a shape" maps directly; optional prompt; canvas up to 25 MP. `fal-ai/flux-2-pro/outpaint` stays as the margins-only alternative (4 MP in and out, no prompt). | Seam quality of both; Bria's handling of a 23 MP input |
| Upscale | `fal-ai/seedvr/upscale/image` ($0.001 per megapixel) as the default; `topaz/upscale/image/precision` (model "Text Refine") when text must stay crisp | Cheapest priced upscaler with factor up to 10 and PNG output; Topaz tiers are published and one model is documented as keeping "text and shapes crisp". | Output dimension rounding; behaviour on already-1280x720 thumbnails |
| Instruction edit ("make the sky purple") | `fal-ai/nano-banana/edit` ($0.039 per image) default; `fal-ai/nano-banana-pro/edit` ($0.15, 1K/2K/4K) for high quality; `fal-ai/flux-pro/kontext` ($0.04) as a second opinion | All three take an image and a prompt; none takes a mask. For a region-limited edit use a masked editor (`fal-ai/ideogram/v3/edit`, `openai/gpt-image-2/edit`, `fal-ai/gpt-image-1.5/edit`) after measuring polarity. | Whether the output keeps the input's dimensions (not stated) |
| Generate thumbnail background / elements with legible text | `fal-ai/ideogram/v3` ($0.03 TURBO / $0.06 BALANCED / $0.09 QUALITY; gallery tags it `typography`) and `fal-ai/nano-banana-pro` ($0.15) | Both are tagged `typography` in the gallery; ideogram has fixed, published tiers. Transparent elements: `fal-ai/ideogram/v3/generate-transparent`. | Actual text fidelity on the target fonts/lengths |
| Editable text from a flat thumbnail (v1.5 candidate) | `fal-ai/ideogram/v3/layerize-text` ($0.09 per image) | Returns the text-free background plus structured text and overlay HTML. | The shape of `text_containers`/`image_layers` (untyped in the schema) |
| Safety gate | `fal-ai/imageutils/nsfw` ($0.001 per image) | Cheapest call found; returns a probability. | Thresholds |

### (b) Not established (full list)

Requested ids whose OpenAPI document returns HTTP 404 (with what was tried in each section above):

- `fal-ai/sam-3-1/image/batch`
- `fal-ai/image-editing/relighting`
- `fal-ai/image-editing/expand`
- `fal-ai/flux-2/pro` (the real id is `fal-ai/flux-2-pro`)
- `fal-ai/image-editing/sketch`
- `fal-ai/invisible-watermark` (listed in the gallery, but its schema 404s, so inputs/outputs are not established)

Also not established: any price for the six endpoints whose page says "$0 per compute second" (`fal-ai/sam2/auto-segment`, `fal-ai/imageutils/rembg`, `fal-ai/inpaint`, `fal-ai/aura-sr`, `fal-ai/creative-upscaler`, and the integrated `fal-ai/lama`); the mask polarity of the endpoints marked NOT STATED in the table near the top; the output dimensions of nearly every generative editor.

Ids that exist but were **not** fetched or documented (seen in the gallery listing only, so nothing about them is established beyond the id): `bria/fibo-edit/*` (edit, erase_by_text, relight, restore, rewrite_text, sketch_to_colored_image), `bria/increase-resolution`, `bria/upscale/creative`, `clarityai/crystal-upscaler`, `fal-ai/flux-lora-fill`, `fal-ai/flux-general/inpainting`, `fal-ai/flux-kontext-lora/inpaint`, `fal-ai/image-apps-v2/object-removal`, `fal-ai/image-apps-v2/photo-restoration`, `fal-ai/ideogram/v2/edit`, `fal-ai/ideogram/v3/remix`, `fal-ai/luma-photon/reframe`, `fal-ai/qwen-image-edit-plus`, `fal-ai/retoucher`, `topaz/upscale/image/creative`, `topaz/upscale/image/generative`, `topaz/restore/image`, `xai/grok-imagine-image/edit`, `luma/agent/uni-1/v1/edit`.

### (c) fal.ai data-handling / terms pages

- Terms of Service: <https://fal.ai/legal/terms-of-service> (page header: "Last Updated: September 8, 2026"). Read in full by text search. What it says about customer inputs, in paraphrase: customers keep ownership of their input and grant fal a licence to process it **to provide the Services**; separately, the Terms define "Usage Data" as anonymized or aggregated data resulting from use of the Services, which "may include data based on or derived from Customer Input", and give fal the right to use Usage Data to improve the services and to design, develop and offer fal's own products, services and AI models; Customer Input and generated output are classed as the customer's Confidential Information, while Usage Data and Feedback are excluded from that class. The word "train" appears in the Terms only in the definition of "Documentation" and in restrictions on using third-party models' outputs to train competing models (and on replicating those models' training data) - **no sentence states whether customer inputs are or are not used to train fal's or third parties' models**. Third-party model providers (the `is_partner_api` endpoints) may have their own terms; those were not read.
- Privacy Policy: <https://fal.ai/legal/privacy-policy> - no occurrence of "train" in the text; account data is deleted within 30 days of account closure or after two years of inactivity.
- Retention controls: <https://docs.fal.ai/docs/documentation/model-apis/media-expiration> (see Cross-cutting facts above).
- Not found: a separate Data Processing Addendum URL (`https://fal.ai/legal/data-processing-agreement` returned 404; the legal index on the Terms page lists a "Data Processing Addendum" and "Acceptable Use Policy" without a URL that was fetched). Not read.
