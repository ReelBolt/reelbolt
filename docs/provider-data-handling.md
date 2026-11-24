# Provider data handling and the training clause

**Status:** policy surface. This page is the developer-facing half of a disclosure the Terms and the
Privacy Policy also make, and all three read the same table:
[`site/lib/provider-data-handling.ts`](../site/lib/provider-data-handling.ts).

## What this page is for

Every hosted AI vendor we call receives *something* of the customer's. For a video platform that is
often a frame of their footage, a transcript of their speech, or a recording of their voice. What
each vendor is then allowed to do with it is a term in *that vendor's* agreement, not ours, and at
least one vendor we integrate with claims a licence to train its own models on inputs **and**
outputs.

A user is entitled to know that before they run a workflow. So the rule this repository follows is:
**the disclosure lives where the user is, and it names vendors**.

## Where the disclosure is shown

| Surface | What it says | File |
|---|---|---|
| Terms & Conditions | A dedicated section: which parts of a user's content are sent, that each provider handles it under its own terms (linked from the Privacy Policy), and that at least one vendor claims a training licence, so a user who does not want that should not use a step calling that vendor. | `site/app/legal/terms/page.tsx` (section 11) |
| Privacy Policy | A per-provider table (what is sent, a link to the provider's own terms, and the training warning where one applies), plus the sub-processor row naming the vendors. | `site/app/legal/privacy/page.tsx` (section 6.1) |
| This page | The developer-facing statement of the rule, and the process for changing a row. | this file |

The sub-processor list in the Privacy Policy is **derived** from the same table rather than written
out again (`MODEL_PROVIDERS` in `site/lib/legal-placeholders.ts`), so the two cannot name different
vendors.

## Safety screening

Screening is not a capability a step chooses, so the OpenAI row and the Privacy Policy's purposes
section name it separately: up to 20 downscaled frames and the text or transcript of uploads and
created files go to the moderation provider, even for an output nobody opens, and up to 10 minutes of
audio goes to the transcription provider to make that transcript. Originals of videos are not sent.
Keep these sentences in step with [`content-moderation.md`](content-moderation.md).

## The table, and the rule it is written under

A row carries the vendor, what a workflow sends it, and links to the vendor's own terms. **The public
pages do not summarise or paraphrase a vendor's terms**: each provider handles content under its own
terms, and a paraphrase goes stale. The one exception is `trainingWarning`, set only where a vendor's
published terms state a right to train on inputs and outputs. Black Forest Labs is the only vendor
with one today: their FLUX API Service Terms grant the vendor a perpetual, irrevocable, sublicensable
licence over inputs *and* outputs and state outright that inputs and outputs may be used to train and
improve their models. That is stated as a fact, in the same words, on both pages.

**Internal record (not published).** What was and was not read from each vendor's terms stays here,
not on the site: BFL's FLUX API Service Terms were read in full. For the other vendors the terms were
not reviewed clause by clause, so no assurance about training is given anywhere, and none should be
added to the site without reading the vendor's terms first. Adding a *provider kind* to the codebase
is an engineering change; putting customer media through it is a legal one, and the two must not be
conflated.

## Where the image arm puts a frame, and why that row already existed

**The image capabilities did not need a new row; they needed the existing rows to say more.** Black
Forest Labs and MiniMax were both already in the table, so shipping image generation and image
editing changed *what* a workflow sends them rather than *whether* we disclose them: a FLUX row now
receives the source image the user chose plus an instruction when a cover is generated or a subject
is moved, and MiniMax's image endpoint fetches a reference image from a link we give it. Both rows'
`sent` wording was extended to say so, in the same words the two legal pages render, because a row
that describes only prompts and frames would under-describe what a user is actually sending.

## Hosted video enhancement (`VideoUpscale`, `FrameInterpolation`)

The two hosted enhancement capabilities (AI upscale, AI frame generation) send fal.ai the **customer's video**: the
clip to enhance, as a presigned GET URL that is valid for 10 minutes and is fetched by fal's servers, never as an
upload through us. Only video is sent: the audio track is never altered by these capabilities and is remuxed locally
from the untouched source after the enhanced clip comes back. What fal retains, and for how long, is **unknown until
measured** (`X-Fal-Store-IO` is not sent; must-measure row 40 in `docs/fal-ai-models.md`), and fal's training terms
were not reviewed clause by clause, so no assurance about either is given anywhere. fal.ai is already in the table
of vendors for the image and video-generation capabilities, so no new vendor row is needed, but its `sent` wording
must name video input when these steps ship (L3/L4). The endpoints are Topaz and ByteDance (upscale) and RIFE and
FILM (frame interpolation), all hosted by fal; the customer's clip is not sent to those vendors directly.

### Extend past the border (`VideoOutpaint`)

`VideoOutpaint` sends fal.ai the same customer video as the other hosted enhancement capabilities (a 10-minute
presigned GET fetched by fal) **plus the customer's optional extension prompt (at most 500 characters)**. The prompt is
screened by the moderation gate before submit. Consent and egress rules are exactly those of the other hosted
operations (`allowSourceMediaEgress`, the project's recorded consent, the `video_enhance` plan feature). Wan VACE is
the default endpoint and Luma Ray 2 reframe the alternative, both hosted by fal; the clip is not sent to those
vendors directly. Retention is unknown until measured, as above; no assurance is given.

## The documentation rerank (`Rerank` capability)

The assistant's documentation search can reorder its top candidates with a `Rerank`-capability provider
(`docs/platform-docs-search.md`, "Model rerank"). What is sent: the user's latest message (truncated to
500 characters) and the text of at most 20 candidate documentation chunks (each truncated to 600
characters), which can include internal background documents. **Where it goes:** a self-hosted reranker
container on the same host receives it and nothing leaves the host (zero egress); a hosted reranker is an
off-host recipient in the same exposure class as a hosted embedding provider. The provider is an
operator-registered `OpenAICompatible` row, not a vendor the product names, so there is no vendor row in
`site/lib/provider-data-handling.ts`; an operator who registers a hosted reranker owns reading that
vendor's terms first, under the rule above. Sending the corpus to a vendor is a Security-review item, and
the self-hosted deployment is the recommended default for any deployment that cares.

When the Rerank row points at Cloudflare Workers AI (`api.cloudflare.com`, model `@cf/baai/bge-reranker-base`,
the staging configuration), Cloudflare receives that query and those top candidate chunks. It is the same
account and vendor as the Workers AI embeddings row, so the same Cloudflare terms apply.

## Web search (`WebSearch` capability)

Agents can search the web (`docs/web-search.md`). **What leaves the host:** the query string the agent wrote
(1 to 200 characters), which can include words taken from the project or the brief, and the key. Nothing
else: no files, no frames, no transcript, no workspace name. The result text comes back; it is not sent on.

| Recipient | Receives | Retention and training | Confidence |
|---|---|---|---|
| DeepSeek (`api.deepseek.com`, Search API) | The query. | Not confirmed: the Search guide states no storage, caching or training terms and the platform terms were not read. Processed by a provider outside the EU and the US, as `deepseek-provider.md` describes for DeepSeek chat. | `unverified` |
| OpenAI (`api.openai.com`, Responses `web_search`) | The query, sent as the input of a model call with `store: false`. | Not confirmed: only the citation-display rule was read; the API data terms were not. | `unverified` |
| Google (Gemini `generateContent` with `google_search`) | The query, as the prompt of a model call. | Google states it stores prompts, context and output for 30 days to produce grounded results (Gemini API additional terms, read through a summariser). Training on grounded results by the customer is barred, and so are caching and showing results to anyone but the prompting user. Whether Google trains on the prompt is not confirmed. | `unverified` |

A query can carry customer-derived words to a vendor whose terms are unread, so the existing rows
("Anthropic, Google (Gemini), DeepSeek" and "OpenAI and Azure OpenAI" in
`site/lib/provider-data-handling.ts`) already name these vendors (their terms are linked, not summarised); web search adds a
different payload (a short query), not a new vendor. Gemini results are refused by the tool until a
Search Suggestions display exists (`AllowRestrictedGrounding`, default off).

## Hosted audio cleanup (`AudioIsolation` capability)

Hosted voice isolation sends the **customer's audio** to fal. **What leaves the host:** a time-limited presigned GET URL
of the source recording (10-minute lifetime, minted by the caller), which fal fetches, plus the fal key. fal's queue
returns URLs of the result files, which the platform downloads through the SSRF-guarded client (no key sent to the CDN
host) and stores in its own object storage. The free local (ffmpeg) filters send nothing anywhere.

| Recipient | Receives | Retention and training | Confidence |
|---|---|---|---|
| fal (`queue.fal.run`, `fal-ai/elevenlabs/audio-isolation`) | The presigned GET URL of the source audio; fal downloads the recording. | Unknown until measured: how long fal keeps the input and output files, whether either is used to improve models, and whether the ElevenLabs-based model behind the endpoint retains audio were not read from fal's or ElevenLabs' terms. | `unverified` |

Because customer voice recordings are biometric-adjacent personal data, no assurance about retention or training is
given on the site until the terms are read and the retention is measured (the same rule as the table above). The
provider row appears under the existing fal entry in `site/lib/provider-data-handling.ts`; that site table is not part
of this layer's scope (T-003/L3 adds the step and its disclosure).

## Avatars and objects: what goes to which provider

An avatar is a named set of pictures (and for a person also short videos and speech samples) of a person or of an
object, held in our own storage ([`design/avatars.md`](design/avatars.md), [`design/avatar-objects.md`](design/avatar-objects.md)).
Holding the files sends nothing anywhere. Media leaves the host only on the paths below. **No live call has been made
to any provider on any of these paths, so every row is `unverified`**: what each provider keeps, whether it trains on
the inputs, and what its model maker's terms say were not read from the providers' terms, and the `X-Fal-Store-IO: 0`
header is sent but whether fal honours it is unmeasured. The public table (`site/lib/provider-data-handling.ts`) was
updated from this section on 2026-11-17 (Privacy `2026-11-17`, owner's decision); this page is the internal record of what the code does.

**The permission, on every path.** A picture of a person or object is sent to a provider only under one of: the
project's recorded media permission (given once in the project's Avatars tab, withdrawable), a permission given for one
call (`allowMediaEgress` on the request, the assistant's approval card, or an MCP call the member allowed in Settings),
or, in a workflow run, the step's own `AllowSourceMediaEgress` or the run's permission field. Without one the request
is refused (`409 CONSENT_REQUIRED`, or the step fails `EGRESS_REFUSED`) before anything is sent. The one path that asks
for no media permission is the person check at the end of this section.

| Path | Provider and model | What is sent, and how | Retention switch | Confidence |
|---|---|---|---|---|
| A clip that shows avatars, MiniMax reference mode (`POST /v2/video_generation`) | MiniMax | The avatar's reference pictures (up to 9), reference videos (up to 3, at most 15 s in total) and speech samples (up to 3, at most 15 s in total), each as an inline `data:` URI inside the request. The original file bytes, each re-hashed against the consented digest first; not downscaled, metadata not removed. | None documented or sent. | `unverified` |
| A clip that shows avatars, fal reference-to-video | fal, serving `alibaba/wan-3.0/reference-to-video` (default; maker Alibaba), `fal-ai/veo3.1/reference-to-video` and `fal-ai/veo3.1/fast/reference-to-video` (maker Google) | Reference pictures only (no videos or voice), as `data:` URIs inside the request, original bytes, digest-checked, at most 24 MB encoded. | `X-Fal-Store-IO: 0` on every request that carries reference pictures. | `unverified` |
| A clip that shows avatars from a composed first frame (the scene still) | fal image-edit model, default `google/nano-banana-2.1/edit` (maker Google), then the video model of the step | The still is made from up to a few of each subject's pictures, each scaled to at most 1536 px on the long side and re-encoded as JPEG (the file's own camera, place and time metadata is left behind), sent as `data:` URIs with the step's prompt inside a fixed first-party instruction; the finished still (a picture of the subject in the scene) then goes to the video model as its first frame, also as a `data:` URI. | `X-Fal-Store-IO: 0` when a person is among the subjects; not sent for an object-only still or clip. | `unverified` |
| A talking head (a person saying narration) | fal, serving `minimax/h3-max/lip-sync/image-to-video` (default) or `fal-ai/kling-video/ai-avatar/v2/standard` (maker Kuaishou) | A still of the person, made from up to three of their reference pictures by the image-edit model above (scaled to 1536 px, JPEG, `data:` URI), then that still and the narration audio (5 to 15 s), both as `data:` URIs. | `X-Fal-Store-IO: 0` on both calls. | `unverified` |
| The picture editor with avatars or objects (a new picture, or an edit) | fal image-edit model, default `google/nano-banana-2.1/edit` | The chosen subjects' pictures, scaled to 1536 px and re-encoded as JPEG, as `data:` URIs, with the member's instruction inside a fixed frame. A loose reference picture the member adds is not scaled: it goes as a presigned GET link that lives 15 minutes. | `X-Fal-Store-IO: 0` when a person is among the subjects; not sent for objects only. | `unverified` |
| An object variant made by the picture model | fal image-edit model (the default `ImageEdit` row) | Each chosen picture of the object, scaled to 1536 px on the long side (JPEG when it is scaled), as a `data:` URI, with the member's words inside a fixed frame; the first finished result goes along as a reference with the next ones. With videos, the first frame of each video is changed the same way and the default video model makes the video again from it (a `data:` first frame). | `X-Fal-Store-IO: 0` on the picture calls. | `unverified` |
| Taking an object out of a video | fal image segmentation, `fal-ai/sam-3-1/image` is the priced default (maker Meta) | Each of up to 6 stills of the member's video, as a presigned GET link that lives 10 minutes (fal fetches it), with the member's description of the object as the text prompt. Every still is first shown to the Vision provider (below), and only stills that show no person are sent. | None sent (the default fal queue). | `unverified` |
| The person check on an object's pictures | The workspace's default `Vision` provider, whichever company the administrator configured | Each picture of an object, and one frame of each of its videos, with one fixed first-party question; nothing the member typed goes with it. A picture up to 4 MB that is a JPEG, PNG or WebP goes as it is (metadata kept); a larger one is scaled to 1568 px and re-encoded as JPEG. Also each still of an extraction before it leaves, and each frame it cuts. A Vision provider is mandatory for objects: with none set up the object is refused. | None; free to the customer, metered like indexing. | `unverified` |

Higgsfield (only when an administrator selects it as the VideoGeneration provider): the prompt and, on the first-frame
path (`HiggsfieldVideoGenerationClient.cs`, image endpoints), a starting picture, which can be a composed scene still;
the picture is put in Higgsfield's presigned file storage and its link is sent. It has no reference mode, so a clip that
shows avatars in reference mode never goes there ([`video-generation.md`](video-generation.md)). The public table has a
Higgsfield row (2026-11-17).

What stays inside: the signed consent release of a person (a PDF or picture) is never sent to any provider. The
retention and keep-until rules, the delete routine and the per-vendor delete outcome
(`project_avatar_vendor_assets.delete_outcome`) are in [`avatars.md`](avatars.md). The model makers named above (Alibaba,
Google, Kuaishou, Meta) are reached through fal and are not parties we contract with; their own terms for inputs were
not read. Faces and voices may be special-category data: the public Privacy Policy (`2026-11-17`) names fal and the makers; a transfer statement for these
providers is not part of this page.

## Product analytics: what leaves the browser and what Google receives

This section covers first-party analytics and the app's Google Analytics 4 (GA4) property. It is
separate from the model-provider table above. Nothing here loads before the visitor grants the cookie
banner (`localStorage['rf-cookie-consent']` = `granted`), except the marketing site's Google tag,
which runs in Google Consent Mode with all storage denied until the visitor chooses, and not at all after a rejection (see "Marketing site GA4" below). Background:
[`product-events.md`](product-events.md#visits-signup-started-and-acquisition-source).

| Item | Where it lives | What it holds | Retention |
|---|---|---|---|
| `rb_ft` cookie | the visitor's browser | First-touch attribution: sanitized UTM values, referrer host, landing path (no query), visitor id, timestamp. At most 512 bytes. Not `HttpOnly`, because JavaScript writes it. | 30 days from the marketing site; a session cookie when the web signup page sets it (only with granted consent). The Go API clears it after writing `user_attribution`. |
| `rf-visitor-id` | the visitor's `localStorage` | A random UUID v4, not derived from the person or device. | Until the visitor withdraws consent (the site removes it) or clears storage. |
| `product_events` visit rows | Postgres | Visitor id, event name, source label, time. No IP, user agent or user id. | Deleted after 13 months (`Telemetry:VisitRetentionMonths`). |
| `user_attribution` | Postgres | One first-touch row per user, joined to the account. Readable by the BI role. | Lives with the user; removed with the account (`ON DELETE CASCADE`). |
| nginx access logs | a Docker volume on the host | One JSON line per request: time, client address, method, path only (no query string), status, size, referrer host and path, user agent. Capability tokens in paths are redacted. | Daily files, deleted after `NGINX_LOG_RETENTION_DAYS` (default 30). See an operator file. |

### Dashboard GA4

The dashboard (`/app`) is unchanged by Consent Mode: it loads `gtag.js` only when `NEXT_PUBLIC_GA_MEASUREMENT_ID` was set at build time
**and** the stored consent is `granted`. It sets `allow_google_signals` and
`allow_ad_personalization_signals` to `false` and sends page views manually, with the path passed
through the telemetry route redactor (`routePath`) so ids and tokens in a URL do not reach Google.
Every config call and every navigation sets `page_location` to the origin plus the redacted path (no
query string, no hash) and `page_referrer` to empty, before any event. GA is not loaded at all on the
sign-in, password-reset, email-verification, invitation, terms-acceptance, marketing-link and
runner-pairing routes, because those URLs carry tokens; the signup page loads it with the query
stripped. **Operator step:** turn off Enhanced Measurement (outbound clicks, site search, file
downloads, video and form interactions, and page views based on browser history changes) on the GA4 data stream used by the dashboard, or give the
dashboard its own stream. It is a property-side setting that `gtag` configuration cannot disable, and
its events would otherwise carry the browser's own URL.
Withdrawing consent sets Google's `ga-disable-<ID>` flag, which stops further collection; cookies
already written expire on Google's schedule.

Google receives, for a consented visitor: the redacted page path and title, the page origin, the
standard GA4 browser and device signals and IP-derived location that `gtag.js` collects, and the `_ga`
client identifiers. It does not receive the visitor id, the `rb_ft` values, an account id or an
email. The marketing site's GA4 behavior is documented in
[`marketing-site.md`](marketing-site.md#google-consent-mode-v2--ga4-is-on-the-page-until-the-visitor-rejects).

### Marketing site GA4

The marketing site (`/`) loads the Google tag on every page when `NEXT_PUBLIC_GA_MEASUREMENT_ID`
is set, for a visitor who has not rejected, under Google Consent Mode v2: `ad_storage`,
`ad_user_data`, `ad_personalization` and `analytics_storage` are denied until the visitor accepts
the banner, and only `analytics_storage` is ever granted. Google signals and ad personalization
are off in the config. Before a choice, Google receives cookieless pings: the page URL (with any
query string) and title, the referrer, language, screen size, browser and OS details, the
request's IP address with an IP-derived location, and a client id that is random per page load and
not stored. No cookie and no local or session storage. After Reject (stored or made on the page)
nothing more is sent: the script is not loaded on later visits, and on the page itself the opt-out
flag and a request guard drop even a hit that was already queued. After Accept it sets `_ga` and
`_ga_<container>` and measures normally. GA4
Enhanced measurement stays on for the marketing site (operator decision), so the pre-choice
pings include the automatic interaction events (time on page, scrolls, outbound clicks, site search, file
downloads, video engagement, form start and submit); the privacy policy names them. Details and the browser proof:
[`marketing-site.md`](marketing-site.md#google-consent-mode-v2--ga4-is-on-the-page-until-the-visitor-rejects).

Published privacy, terms and cookie text is not changed by this page. Needed wording changes go to
the owner as a separate review item.

## Adding or changing a row

1. Read the vendor's own terms page. If it states a training right, record the clause, not a summary of it.
2. Update `site/lib/provider-data-handling.ts`: the row, its `sent` wording, its `terms` links and, only
   if the vendor's terms state a training right, its `trainingWarning`.
3. Nothing else needs to change. The Terms, the Privacy Policy and the sub-processor list all read
   from that file, so all three move together by construction.

## What is deliberately not on this page

**Legal advice.** This page records what vendors' published terms say and where that was read. It
does not decide whether a given vendor is acceptable for a given customer, jurisdiction or contract,
and it is not a substitute for a DPA review. The plan that raised this
([`docs/thumbnails-and-image-editing.md`](thumbnails-and-image-editing.md)) flags the Black Forest
Labs clause as "a data-protection question, not a pricing one", and the owner's decision was to ship
that integration **with** this disclosure rather than to pretend the clause is not there.

**A per-plan or per-organization control.** There is no switch today that prevents one run from using
a training-claim vendor while another does not. If that is wanted, it belongs on the provider row
(a per-organization override), not on this page.

**The consent for it is recorded per project, on the server.** A hosted enhancement uploads the whole source file, so the
`/enhance` route refuses a hosted operation with `409 CONSENT_REQUIRED` unless the project's `video_enhance_consent_at` is set,
and only then writes `allowSourceMediaEgress: true` into the step it queues. The engine enforces it too: a hosted VideoEnhance step of ANY workflow kind (an editor's own workflow included) is refused `EGRESS_REFUSED` unless both the step flag and the project consent are set, the consent read fresh before the upload. The timestamp is written by the UI-only routes
`GET|PUT|DELETE /api/v1/projects/{p}/video-enhance/consent` (a separate consent from magic blur's); the assistant and MCP can
read it but never grant it, and withdrawing it stops the project's queued and running hosted enhancements of every workflow kind. The free local
operations never upload and never ask for it.
