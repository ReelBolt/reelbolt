# Thumbnails, image models and the AI image editor

**Status:** research and plan only. **No feature code is written.** This document is the deliverable
the owner approves before any work package starts.
**Date:** 2026-10-09. **Branch:** `thumbs-research`.
**Tracking:** issue #227 in the project's private tracker.

This page answers four questions: which image models we could actually use, how the "3D-aware
editing, like Higgsfield" effect is *actually* produced, where all of this fits the existing
inference-provider architecture, and what the smallest shippable first slice is.

Every claim about what a model can do or what a licence permits carries a URL. Where a claim could
not be established it says so — an unverified claim is worse than an admitted gap, and the licence
question in particular is a legal one, not a technical one.

---

## Table of contents

- [The four model families, and why they must not be conflated](#the-four-model-families-and-why-they-must-not-be-conflated)
- [Licence survey: open weights are not one thing](#licence-survey-open-weights-are-not-one-thing)
- [Model survey: generation](#model-survey-generation)
- [Model survey: editing and inpainting](#model-survey-editing-and-inpainting)
- [Model survey: segmentation and matting (select the subject)](#model-survey-segmentation-and-matting-select-the-subject)
- [Model survey: depth and 3D](#model-survey-depth-and-3d)
- [Model survey: camera-controlled video (the Higgsfield family)](#model-survey-camera-controlled-video-the-higgsfield-family)
- [Hosted providers we can call without hosting anything](#hosted-providers-we-can-call-without-hosting-anything)
- [The 3D-aware editing question, answered honestly](#the-3d-aware-editing-question-answered-honestly)
- [Architecture fit](#architecture-fit)
- [The GPU question, said loudly](#the-gpu-question-said-loudly)
- [Cost](#cost)
- [Storage, metering and retention](#storage-metering-and-retention)
- [Phased recommendation](#phased-recommendation)
- [What could not be verified](#what-could-not-be-verified)
- [Sources](#sources)

---

## The four model families, and why they must not be conflated

The single most important thing in this document is that these are **four different products from
four different vendors with four different licences**, and a plan that treats "an image model" as one
thing will pick a licence it cannot ship.

| Family | Input → output | Answers | Example |
|---|---|---|---|
| **Generation** | text (+ optional reference image) → image | "make me a cover" | Qwen-Image, FLUX.1 [schnell], MiniMax `image-01` |
| **Editing / inpainting** | image (+ mask and/or instruction) → image | "change this part of this picture" | Qwen-Image-Edit, FLUX.1 Kontext, LaMa |
| **Segmentation / matting** | image (+ point or label) → mask(s) | "which pixels are the person" | SAM 2, SAM 3, RMBG-2.0, BiRefNet |
| **Depth / 3D** | image → depth map, point cloud, mesh | "how far away is each pixel" | Depth Anything V2, Depth Pro, TRELLIS |

A vendor that is excellent at one is usually absent from the others. Qwen-Image-Edit cannot segment
a person; SAM 2 cannot generate an image; Depth Anything cannot inpaint. The provider architecture
already encodes exactly this idea — see [Architecture fit](#architecture-fit) — so the plan adds one
capability per family rather than one "Images" capability.

There is a **fifth** family, video camera control, which is where the Higgsfield question actually
lives. It is covered separately and honestly in
[Model survey: camera-controlled video](#model-survey-camera-controlled-video-the-higgsfield-family).

---

## Licence survey: open weights are not one thing

"Open source image model" is not a licence. The repo already had to learn this once, for Fish Audio:
the *hosted* API has no documented non-commercial restriction, while *self-hosting* Fish's open-weight
models is covered by the Fish Audio Community License Agreement (free for research and non-commercial use and
for limited commercial use below its annual-revenue threshold; above it, and for a hosted service, a separate
licence from Fish Audio is needed). Those are two different things and
[`docs/voiceover.md`](voiceover.md) documents both separately. Image models need the same treatment,
because the split here is even more common.

Three licence shapes matter to us:

1. **Genuinely permissive** (Apache-2.0, MIT) — we may self-host the weights in a commercial
   product. Qwen-Image, Z-Image, HiDream-I1, Lumina-Image 2.0, SAM 2, Depth Anything V2, TRELLIS,
   ReCamMaster, LaMa, IC-Light.
2. **Restricted but commercially usable** (vendors' own community licences with revenue thresholds,
   or OpenRAIL variants with use restrictions) — SDXL (`openrail++`), Stable Diffusion 3.5
   (`stabilityai-ai-community`). These are usable but impose behavioural conditions we would have
   to read and honour.
3. **Non-commercial** — the whole FLUX `[dev]` line. This is the one that will bite.

**FLUX `[dev]` is non-commercial, and that is broader than most people assume.** Black Forest Labs'
[FLUX \[dev\] Non-Commercial License v2.0](https://bfl.ai/legal/non-commercial-license-terms)
(revised 25 November 2025) defines "Models" to include *"FLUX.1 [dev], FLUX.1 Fill [dev],
FLUX.1 Depth [dev], FLUX.1 Canny [dev], FLUX.1 Redux [dev], ... FLUX.1 Kontext [dev],
FLUX.1 Krea [dev], and FLUX.2 [dev]"*. So the editing model (Kontext) **and** the depth model
(FLUX.1 Depth) sit under the same non-commercial term as the base generator — a plan that says "we
use FLUX for inpainting" is a plan that cannot ship commercially. The licence's own definition of
"Non-Commercial Purpose" excludes any "revenue-generating activity", any "direct interactions with
or that has impact on end users", and training/fine-tuning/distilling for commercial use.

The permissive escape hatches in the same family: **FLUX.1 [schnell] is Apache-2.0** — its
Hugging Face model page states `License: apache-2.0`
([huggingface.co/black-forest-labs/FLUX.1-schnell](https://huggingface.co/black-forest-labs/FLUX.1-schnell))
— but schnell is generation-only and weaker than the [dev] line.

**SAM 2 and SAM 3 have different licences, and this is easy to get wrong.** SAM 2 is Apache-2.0
([`facebookresearch/sam2/LICENSE`](https://github.com/facebookresearch/sam2/blob/main/LICENSE)).
SAM 3 (released alongside SAM 3D on 19 November 2025) is **not** — it ships under the
["SAM License"](https://github.com/facebookresearch/sam3/blob/main/LICENSE), a Meta proprietary-free
licence (ScanCode keys it `LicenseRef-scancode-sam-2025-11-19`, category *Proprietary Free*, owner
Facebook: [LicenseDB](https://scancode-licensedb.aboutcode.org/sam-2025-11-19.html)). It grants broad
rights but is not an OSI licence, carries redistribution conditions, and is the same licence family
Meta uses for Llama. Treat it as "read before shipping", not as "Apache".

**Depth Pro is Apple's own licence** (`apple-amlr` on
[huggingface.co/apple/DepthPro](https://huggingface.co/apple/DepthPro)), not Apache, despite the
Depth Anything family next door being Apache-2.0.

### The rule this document proposes

Mirror the Fish Audio precedent exactly, in the admin form and in this doc:

> **Hosted endpoint and self-hosted weights are two separate licence questions.** A provider row's
> kind determines which one you are answering, and the admin UI must say which. Where the weights are
> non-commercial (the FLUX `[dev]` family), we may call a *hosted* FLUX endpoint under that vendor's
> commercial API terms, but we may not bake the weights into a product we ship or run for revenue.

---

## Model survey: generation

| Model | Licence | Self-hostable | Rough hardware | Provider fit |
|---|---|---|---|---|
| **Qwen-Image / Qwen-Image-2512** | Apache-2.0 ([model card](https://huggingface.co/Qwen/Qwen-Image)) | Yes | 20B MMDiT — datacentre GPU | operator container via a new kind |
| **Qwen-Image-2.0** (2026-02-10) | not checked — successor release, licence not verified in this session | unknown | "lighter architecture" claimed by Qwen | — |
| **Z-Image-Turbo** (Tongyi-MAI) | Apache-2.0 ([model card](https://huggingface.co/Tongyi-MAI/Z-Image-Turbo)) | Yes | small — single-stream DiT | operator container |
| **Lumina-Image 2.0** | Apache-2.0 ([model card](https://huggingface.co/Alpha-VLLM/Lumina-Image-2.0)) | Yes | 2B params — the lightest on this list | operator container, or CPU-ish |
| **HiDream-I1** | MIT ([model card](https://huggingface.co/HiDream-ai/HiDream-I1-Full)) | Yes | 17B params | operator container |
| **FLUX.1 [schnell]** | Apache-2.0 ([model page](https://huggingface.co/black-forest-labs/FLUX.1-schnell)) | Yes | ~12B | operator container; **hosted BFL API** |
| **FLUX.1 [dev] / FLUX.2 [dev]** | **FLUX \[dev\] Non-Commercial** ([licence](https://bfl.ai/legal/non-commercial-license-terms)) | Weights yes, commercially **no** | ~12B+ | **hosted BFL API only** |
| **Stable Diffusion XL 1.0 base** | `openrail++` ([model card](https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0)) | Yes | ~3.5B | operator container |
| **Stable Diffusion 3.5 Large** | `stabilityai-ai-community` ([model page](https://huggingface.co/stabilityai/stable-diffusion-3.5-large)) | Yes (gated) | ~8B | operator container; hosted Stability API |
| **MiniMax `image-01`** | proprietary hosted service | No | — | **already-integrated kind** |

**Text rendering is a real requirement for thumbnails, and it narrows the field.** Cover images
routinely carry words, and most diffusion models mangle them. Qwen-Image's own model card leads with
"significant advances in **complex text rendering**" and "precise image editing"
([README](https://raw.githubusercontent.com/QwenLM/Qwen-Image/main/README.md)). Ideogram and Recraft
are the other two names associated with in-image text. This should drive model choice for thumbnails
specifically, and it is the reason a thumbnail feature should not simply reuse whatever model the
platform already runs for other art.

---

## Model survey: editing and inpainting

| Model | Licence | Operation | Self-hostable | Provider fit |
|---|---|---|---|---|
| **Qwen-Image-Edit-2511** | Apache-2.0 ([model card](https://huggingface.co/Qwen/Qwen-Image-Edit-2511)) | instruction edit | Yes, 20B | operator container |
| **LaMa** | Apache-2.0 ([LICENSE](https://github.com/advimman/lama/blob/main/LICENSE)) | **mask** inpainting | Yes, small | operator container — the cheap classical option |
| **IC-Light** | Apache-2.0 ([LICENSE](https://github.com/lllyasviel/IC-Light/blob/main/LICENSE)) | relighting | Yes | operator container |
| **HiDream-E1.1** | not verified in this session | instruction edit | Yes | — |
| **FLUX.1 Fill [dev]** | **non-commercial** ([licence](https://bfl.ai/legal/non-commercial-license-terms)) | mask inpainting | NO commercially | hosted BFL API only |
| **FLUX.1 Kontext [dev]** | **non-commercial** (same licence) | instruction edit | NO commercially | hosted BFL API only |
| **FLUX.1 Depth [dev] / Canny [dev]** | **non-commercial** (same licence) | structure-conditioned | NO commercially | hosted BFL API only, if offered |

Two observations that shape the plan:

- **LaMa is the sleeper.** It is small, Apache-2.0, and does exactly one thing (fill a masked
  region), which is precisely what "remove the object behind the subject after I move it" needs. It
  is far cheaper to host than a 20B instruction editor and it is the operation the editor calls most
  often.
- **Mask-based inpainting is a different capability from instruction editing**, and the two are not
  interchangeable at the API level: one takes a mask, the other takes a sentence. If the plan wants
  both, it is buying two things.

---

## Model survey: segmentation and matting (select the subject)

This is the family that turns "the owner wants to select subjects" into a concrete dependency.

| Model | Licence | What it does | Self-hostable | Notes |
|---|---|---|---|---|
| **SAM 2 / SAM 2.1** | **Apache-2.0** ([LICENSE](https://github.com/facebookresearch/sam2/blob/main/LICENSE)) | promptable segmentation, **images and video**, streaming memory | Yes | the video half matters: a subject selected in one frame can be tracked across a clip |
| **SAM 3** | **SAM License** — *not* Apache ([LICENSE](https://github.com/facebookresearch/sam3/blob/main/LICENSE)) | open-vocabulary / concept segmentation | Yes | licence review required before shipping |
| **Grounding DINO** | Apache-2.0 ([LICENSE](https://github.com/IDEA-Research/GroundingDINO/blob/main/LICENSE)) | text → bounding box (open-vocabulary detection) | Yes | the natural "find the person" front-end that seeds SAM |
| **BiRefNet** | MIT ([model card](https://huggingface.co/ZhengPeng7/BiRefNet)) | dichotomous image segmentation / matting | Yes | high-resolution alpha mattes |
| **RMBG-2.0** (BRIA) | `bria-rmbg-2.0` — **custom, not permissive** ([model page](https://huggingface.co/briaai/RMBG-2.0)) | background removal | Yes | read the licence: BRIA's model licences historically restrict commercial use absent an agreement |

**We already have a weaker version of this and it is worth naming.** The engine's
`IObjectLocator` / `VisionObjectLocator`
(`inference/src/ReelBolt.WorkflowEngine/Services/Video/Tracking/VisionObjectLocator.cs`) asks a
**Vision chat provider** for boxes on a 0–1000 grid and uses them as tracker seeds. That is
bounding boxes, not masks, and it is model-driven prose parsing rather than a segmentation model —
the file's own comment says a model "can only ever contribute boxes". It is enough to *centre a
crop* on a subject. It is **not** enough to cut a subject out onto a transparent layer. The gap
between "box" and "alpha matte" is exactly the new capability.

---

## Model survey: depth and 3D

| Model | Licence | Output | Hardware | Notes |
|---|---|---|---|---|
| **Depth Anything V2 Small** | Apache-2.0 ([LICENSE](https://github.com/DepthAnything/Depth-Anything-V2/blob/main/LICENSE), [model card](https://huggingface.co/depth-anything/Depth-Anything-V2-Small)) | relative depth | **24.8M params** — plausibly CPU-feasible for a single still | the cheapest possible entry point |
| **Depth Anything V2 Base / Large** | Apache-2.0 (same repo) | relative depth | 97.5M / 335.3M | |
| **Depth Anything V2 Giant** | — | — | — | README says "Coming soon" — **does not exist as of the page I read** |
| **Video Depth Anything** | Apache-2.0 ([LICENSE](https://github.com/DepthAnything/Video-Depth-Anything/blob/main/LICENSE)) | **temporally consistent** depth for long video | GPU | the consistency is the point; per-frame depth flickers |
| **Depth Pro** (Apple) | `apple-amlr` ([model card](https://huggingface.co/apple/DepthPro)) | **metric** depth, sub-second | GPU | metric scale without camera intrinsics |
| **Prompt Depth Anything** | not verified | 4K metric depth with LiDAR prompt | — | LiDAR prompt makes it irrelevant to us |
| **TRELLIS** (Microsoft) | **MIT** ([LICENSE](https://github.com/microsoft/TRELLIS/blob/main/LICENSE)) | image/text → 3D asset (radiance fields, Gaussians, **meshes**), with local 3D editing | GPU | the only genuinely permissive image→**mesh** path found |
| **3D Photo Inpainting** (Shih et al.) | **MIT** ([LICENSE](https://github.com/vt-vl-lab/3d-photo-inpainting/blob/main/LICENSE)) | depth + inpainting → **Layered Depth Image**, rendered with a virtual camera | modest | this is the classical, cheap, honest "2.5D" technique — see the 3D section |
| **SAM 3D Objects / SAM 3D Body** | SAM License ([Meta blog](https://ai.meta.com/blog/sam-3d/)) | single image → 3D object / parametric human body | GPU | released 19 Nov 2025; licence review required |

---

## Model survey: camera-controlled video (the Higgsfield family)

This is a **video** family, not an image family, and it is the one the owner pointed at.

| Model | Licence | What it does | Self-hostable |
|---|---|---|---|
| **ReCamMaster** (Kuaishou, ICCV'25 Oral) | **MIT** ([LICENSE](https://github.com/KwaiVGI/ReCamMaster/blob/main/LICENSE)) | re-captures a video along a new camera trajectory | Yes; built on Wan 2.1 |
| **GEN3C** (NVIDIA, CVPR'25 Highlight) | **Apache-2.0** ([LICENSE](https://github.com/nv-tlabs/GEN3C/blob/main/LICENSE)) — *code only; the weights it runs are Cosmos, a separate licence I did not verify* | 3D-informed world-consistent video with precise camera control | Yes; needs a GPU |
| **TrajectoryCrafter** (ICCV'25 Oral) | **could not verify** — no `LICENSE` at the repo root ([404](https://github.com/TrajectoryCrafter/TrajectoryCrafter)) | redirects the camera trajectory of a monocular video | Yes; **README recommends VRAM ≥ 28 GB** |
| **Wan 2.2** | Apache-2.0 ([model card](https://huggingface.co/Wan-AI/Wan2.2-T2V-A14B)) | base video generation | Yes |
| **Uni3C** (Alibaba DAMO, SIGGRAPH Asia 2025) | not verified | unifies 3D camera + human motion control | not verified |

ReCamMaster's README enumerates exactly ten basic trajectories — Pan Right, Pan Left, Tilt Up, Tilt
Down, Zoom In, Zoom Out, and four more — which is the same shape of product surface as the
"45+ camera motion presets" that Higgsfield launched with (the count is from a Japanese trade
writeup, [cgworld.jp](https://cgworld.jp/flashnews/01-202504-Higgsfield-AI.html), and that specific
number is **third-party, not vendor-confirmed**).

---

## Hosted providers we can call without hosting anything

A hosted provider is the only path that needs **no GPU, no new container and no new licence
decision** — which, given [the GPU section](#the-gpu-question-said-loudly), makes it the only path
that fits our current cloud scope.

### MiniMax is the standout, because we already integrate it

`InferenceProviderKind.MiniMax` already exists and already serves `VideoGeneration` over
`https://api.minimax.io` with `Authorization: Bearer`. MiniMax also ships an **image** API and a
**camera-reference** video API:

- `POST https://api.minimax.io/v1/image_generation` with model `image-01` — text-to-image
  (`aspect_ratio`, `n`, `response_format: url|base64`, `prompt_optimizer`) **and**
  image-to-image via `subject_reference`, preserving a reference subject's characteristics
  ([Image Generation Guide](https://platform.minimax.io/docs/guides/image-generation),
  [Text-to-Image API reference](https://platform.minimax.io/docs/api-reference/image-generation-t2i)).
- The video models' **Reference Generation** mode accepts reference *images, videos or audio* and the
  guide lists "reference character, **motion, camera**, style, voice, or editing rhythm" as the things
  it can reference ([Video Generation guide](https://platform.minimax.io/docs/guides/video-generation)).
- The text-to-video prompt schema itself carries bracketed camera directives — the documented example
  is `"A man picks up a book [Pedestal up], then reads [Static shot]."`
  ([Text to Video API reference](https://platform.minimax.io/docs/api-reference/video-generation-t2v)).

That last point matters more than it looks: **we already have a camera-control surface today**, in
the provider we already ship, expressed as prompt directives.

### Higgsfield's own API is generation-only

Higgsfield's public API catalog documents "84 entries: 17 Image and 67 Video" and lists image
generation as SOUL, Soul ID training, Marketing Studio, Ads Studio, Grok, Recraft, Qwen Image,
Ideogram and Z-Image ([Model API Reference](https://docs.higgsfield.ai/docs/models)). Their site
advertises a "3D Jutsu" product, but **I could not find any public API documentation for it**, and
I could not fetch their blog or help centre (both render client-side and returned no content to a
plain fetch). So: Higgsfield is a *product* reference for the interaction the owner described, not
corroborated evidence about its internals, and not currently an infrastructure option for us.

### Others

The hosted image API landscape beyond MiniMax (OpenAI GPT Image, Google's Gemini/Imagen image
models, Black Forest Labs' own API, fal.ai, Replicate, Stability, Ideogram, Recraft) was surveyed in
a parallel pass; **the authoritative table with fetched pricing URLs is
[Hosted image APIs: the fetched comparison](#hosted-image-apis-the-fetched-comparison)** at the end
of this document. The architectural point is settled regardless of the individual prices: every one
of them is a **stateless, credentialled HTTPS call**, which is exactly what
`IVideoGenerationClientFactory` + `MiniMaxVideoGenerationClient` already are.

---

## The 3D-aware editing question, answered honestly

**The owner's description:** select subjects (people, objects), turn them into layers, and edit them
in a 3D-aware way, "kinda like Higgsfield does for videos".

**The honest answer has three parts, and only the first is currently reachable.**

### What Higgsfield actually does (as far as can be established)

I could not obtain a primary technical statement from Higgsfield. Their blog and help centre render
client-side and returned no readable content; their API documentation is a generation catalog with no
camera-control or 3D endpoint documented. What *was* verifiable: their launch product was
image-to-video with a large library of camera-motion presets, and they later shipped a Blender plugin
for authoring camera moves - both reported by third parties
([cgworld.jp](https://cgworld.jp/flashnews/01-202504-Higgsfield-AI.html),
[ai-primer.com](https://www.ai-primer.com/creative/stories/higgsfield-blender-camera-control)), and
their own public catalog confirms the product is a generation suite.

**So the claim "Higgsfield does 3D editing of subjects" is not something I can confirm.** What the
open literature says the effect *is* is more useful than what any marketing page says, and it
contradicts the natural reading of the request.

**Our own repo already classifies Higgsfield as a video-generation vendor, not an editor.**
[`docs/video-generation.md`](video-generation.md) lists *"Higgsfield, and any provider other than
MiniMax"* under "Out of scope for phases 3, 4 and 5", with the note that `MiniMax` is still the only
`VideoGeneration` provider kind; and it records that keyframe transport is *"wave B's work -
Higgsfield's presigned upload is where it lands"*. The H7 legal work also already names Higgsfield in
the sub-processors list. There is **no**
`Higgsfield` member in `InferenceProviderKind` today — a grep of `inference/src/` finds none.

The consequence is that "like Higgsfield" in our own roadmap means **a hosted video-generation
provider we would add as a `VideoGeneration` kind**, which is a different feature from the image
editor this document otherwise plans. It is worth separating the two in the owner's mind: adding
Higgsfield as a video provider is a small, well-understood extension of an existing capability
(keyframe transport is the only real work); building a layer-based 3D image editor is not.

### What the effect actually is, per the primary sources

The camera-control class of models is **not** a mesh, and **not** a set of editable layers. It is a
pipeline in three stages, and the "3D" lives in an intermediate representation, not in the output.

**GEN3C** (NVIDIA, CVPR 2025 Highlight) states its method in its own abstract: *"We achieve this with
a 3D cache: point clouds obtained by predicting the pixel-wise depth of seed images or previously
generated frames. When generating the next frames, GEN3C is conditioned on the 2D renderings of the
3D cache with the new camera trajectory provided by the user."* The method overview adds that it
first builds a spatiotemporal 3D cache by predicting depth for each image and unprojecting it into 3D,
then renders the cache along the user's camera poses, and feeds those renderings into a video
diffusion model.
([research.nvidia.com/labs/toronto-ai/GEN3C](https://research.nvidia.com/labs/toronto-ai/GEN3C/))

So the pipeline is:

1. **Depth estimation** turns each frame into a depth map.
2. **Unprojection** lifts pixels into a 3D representation (point cloud / layered depth image).
3. **Re-render** from a new virtual camera.
4. **A generative model fills the holes** (disocclusions) that step 3 exposes, and makes the result
   photoreal.

**ReCamMaster** (Kuaishou, ICCV'25 Oral) is the same idea framed as re-capture: *"re-capture
in-the-wild videos with novel camera trajectories, achieved through our proposed simple-and-effective
video conditioning scheme."* It is MIT-licensed and built on Wan 2.1.
([README](https://github.com/KwaiVGI/ReCamMaster))

**TrajectoryCrafter** (ICCV'25 Oral) generates *"high-fidelity novel views from casually captured
monocular video, while also supporting highly precise pose control"* and its README recommends a GPU
with **>= 28 GB VRAM**. Its own README lists its limitation plainly: *"since it is built upon a
pretrained video diffusion model, it may struggle with complex cases that go beyond the generation
capabilities of the base model."* ([README](https://github.com/TrajectoryCrafter/TrajectoryCrafter))

**Consequences for the owner's ask, stated plainly:**

| The owner asked for | What the models actually do | Reachable today? |
|---|---|---|
| "Select subjects, turn them into layers" | Segmentation produces a **mask**; a mask plus the source composited with alpha *is* a layer. This is genuinely achievable and does not need 3D at all. | **Yes** - needs a segmentation capability we do not have |
| "Edit them in a 3D-aware way" - move a subject and see the background behind it | Mask + **inpainting** the hole. Nothing 3D is required and this is the operation that actually delivers the user-visible result. | **Yes** - needs an inpainting capability |
| "3D-aware" as in **parallax / depth / a change of viewpoint** | Depth -> layered depth image -> re-render from a virtual camera. This is the classical "3D Photography" technique and it is **MIT-licensed**. | **Partially** - needs a depth capability; CPU-feasible only at small model sizes |
| "Like Higgsfield does for videos" - a camera move *through* a shot | A **video diffusion model conditioned on camera pose**, plus a depth-driven 3D cache. Requires a GPU and a model none of our providers serve. | **No** - see below |

### What we would need and do not have

Camera-controlled video re-shoot (GEN3C / ReCamMaster / TrajectoryCrafter class) requires **all** of:

1. a GPU host (TrajectoryCrafter names >= 28 GB VRAM);
2. a video diffusion base model (Wan 2.2 / Cosmos / CogVideoX class);
3. per-frame or temporally-coherent depth (Video Depth Anything class);
4. a 3D cache and a renderer.

We have **none** of these. Our cloud deployment deliberately runs **no local model containers at
all** - an operator file disables `whisper` and `embeddings` under a
never-activated profile, with the stated reason that a CPU model server *"steals engine CPU"* and
that hosting is replaced by hosted provider rows. Our compute topology is CPU-only: ffmpeg in the
engine container plus 2-CPU/2-GB Remotion sandbox containers
([`docs/design/cloud-provider-adr.md`](design/cloud-provider-adr.md)).

**Therefore: a true 3D-aware, camera-controlled video edit is out of scope until either (a) a hosted
vendor exposes it as an API, or (b) the owner decides to fund GPU hosting.** That is a founder
decision, not an engineering one, and the plan below does not depend on it.

### What *is* honestly achievable, in order of cost

1. **Layers without 3D.** Segmentation -> alpha layer -> move / scale / recolour / delete, with
   inpainting to repair the hole. Qwen-Image-Layered demonstrates exactly this operation set -
   *"decompose an image into multiple RGBA layers ... each layer can be independently manipulated
   without affecting other content ... high-fidelity elementary operations such as resizing,
   reposition, and recoloring"* - and it is **Apache-2.0**
   ([model card](https://huggingface.co/Qwen/Qwen-Image-Layered)). Its own card also shows "replace
   the second layer from a girl to a boy (the target layer is edited using Qwen-Image-Edit)". This is
   **2D, not 3D**, and it is what most users will actually perceive as "layers I can edit".
2. **2.5D parallax from a still.** Depth Anything V2 depth + the MIT-licensed
   [3D Photo Inpainting](https://github.com/vt-vl-lab/3d-photo-inpainting) layered-depth pipeline ->
   a small camera move with filled disocclusions. This is the honest, cheap version of "3D-aware" and
   it is the one I would put in front of the owner as a demo before promising anything.
3. **A real 3D asset.** TRELLIS (MIT) or SAM 3D Objects (SAM License) turn a selected subject into a
   mesh, which *is* genuinely 3D and *would* let you rotate a subject independently. Both need a GPU,
   and the SAM licence needs review.
4. **Camera-controlled video.** Requires funding a GPU. Not recommended now.

**One correction to a common expectation, because it matters:** a "3D-aware" edit in this family
does not preserve the scene's true 3D geometry. It *invents* what is behind the subject. If the owner
expects the edit to be geometrically faithful - the subject moved and the true background revealed -
the technology delivers a plausible fabrication, not a reconstruction. Every one of GEN3C,
ReCamMaster and TrajectoryCrafter is a **generative** method; TrajectoryCrafter says so in its own
limitations section.

---

## Architecture fit

### Where it lives

Two services, split the way the rest of the feature already is:

| Concern | Home | Why |
|---|---|---|
| Client abstractions + factories + resolver | `inference/src/ReelBolt.Shared/Inference/` | Both services resolve providers; every existing capability's client lives here |
| Provider CRUD, capability validation, test endpoint, admin UI | `ReelBolt.Inference.Api` | `InferenceProvidersController` already owns this |
| Thumbnail generation as a workflow step | `ReelBolt.WorkflowEngine` | Steps are the engine's job; `VideoAnalyze` already extracts the frames |
| Thumbnail/derivative rows | `ReelBolt.Inference.Api` (`media_derivatives`) | The table is Inference-API-owned and **already exists with no writer** |
| The image editor document | `ReelBolt.Inference.Api` | Same as `edit_timelines` |

**`media_derivatives` is the single most important existing artefact for this feature.** It already
exists (`ReelBolt.Shared/Data/Models/MediaDerivative.cs`), it already has
`MediaDerivativeKind.Thumbnail` ("A representative thumbnail frame"), it already carries
`StorageKey`, `StorageBucket`, `MimeType`, `SizeBytes`, `Width`, `Height` - and per
[`docs/editor.md`](editor.md) and `StorageRetentionService` it has **no writer yet**. A thumbnail
feature is therefore *finishing a table that was designed for it*, not inventing storage.

### What we already have that a thumbnail feature needs

This is the part of the plan that makes the first slice cheap, and it is worth being explicit because
it is not obvious from the outside:

| Need | Already exists | Where |
|---|---|---|
| Extract a frame at time *t* at a capped width | `IKeyframeExtractor.ExtractKeyframeAsync(workspace, in, out, atSec, maxWidth)` | `Services/Video/IKeyframeExtractor.cs` |
| Extract N frames as a contact sheet | `IKeyframeExtractor.ExtractContactSheetAsync` | same file |
| Shot boundaries, pacing, per-shot visual descriptors, **take quality**, sharpness, duplicate grouping, look grouping | `FrameGridAnalyzer` (pure, no ffmpeg) | `Services/Video/FrameGridAnalyzer.cs` |
| "Where is this subject?" | `IObjectLocator` / `VisionObjectLocator` - boxes on a 0-1000 grid from a **Vision** provider | `Services/Video/Tracking/VisionObjectLocator.cs` |
| Run all of it **on a runner** instead of the engine host | `IMediaWorkspace` (D9a) + `RemoteMediaWorkspace` (D11) | `Services/Compute/` |
| Verify a produced artefact before promoting it | `IOutputVerifier` (D12) | `Services/Compute/` |
| Meter whatever it costs | `IUsageRecorder` + `IRateCardService` (B3b/B3c/B4) | `Shared/Metering/` |

**Read that table again before approving a scope.** The expensive, hard, media-plumbing parts of
"generate a thumbnail" are already built and already runner-capable. What is missing is the selection
policy, the derivative writer, and - only if we want a *generated* cover rather than a *selected*
frame - an image model.

### The new capabilities

Four, appended, one per family. The repo's own rule is that a capability is a **role**, not a model:
`Enums.cs`'s comment on `InferenceProviderCapability` says the discriminator "is load-bearing, not
cosmetic: it determines which resolution path a provider is eligible for, and which 'one default row'
uniqueness constraint it participates in". A segmentation model is never a chat model, so folding
these together would break that rule.

| New capability | Resolution method | Serves |
|---|---|---|
| `ImageGeneration` | `ResolveImageGenerationAsync` | text (+ optional subject reference) -> image |
| `ImageEdit` | `ResolveImageEditAsync` | image + mask and/or instruction -> image |
| `Segmentation` | `ResolveSegmentationAsync` | image (+ point/label) -> mask |
| `Depth` | `ResolveDepthAsync` | image -> depth map |

Each follows the established three-rung precedence - **step/leaf explicit id -> the single default row
for that capability -> none** (no legacy config fallback, for the same reason transcription and vision
have none: silently sending an image to a chat deployment produces a confusing 404). The enum is
persisted as a string with a composite unique index on `(capability, is_default)`, so **adding an
enum member needs no EF migration** - the same note already recorded for `Vision` in `Enums.cs`.

**Deferring one question deliberately:** whether `ImageEdit` should absorb mask-based inpainting or
whether that deserves its own capability. Recommendation: **one capability, two operations in the
client interface** (`EditAsync(instruction)` and `InpaintAsync(mask)`), because the same vendor row
usually serves both and a second capability would double the admin surface for one deployment. If a
future hosted vendor offers only one of the two, the client throws a typed `NotSupportedException`
exactly as `TranscriptionClientFactory` already does for Anthropic.

**Superseded for the picture editor (2026-10-10).** Once the editor shipped nine tools on one `ImageEdit`
row, the cost of the single capability showed: one row's model was sent to an instruction edit, an object
removal, an outpaint and an upscale alike. Each editor tool now resolves its own capability (`ImageErase`,
`ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`, with `ImageEdit` kept
for the instruction edit), with a fallback to the `ImageEdit` default only where that row's kind can do the
job. See `docs/editor.md` "Thumbnail editor operations".

### Client factories and client kinds

| Kind | `ImageGeneration` | `ImageEdit` | `Segmentation` | `Depth` |
|---|---|---|---|---|
| `MiniMax` (exists) | **yes** - `image-01` | yes (image-to-image via `subject_reference`) | no | no |
| `OpenAICompatible` (exists) | yes - OpenAI Images API shape (`POST {base}/images/generations`) | yes - `POST {base}/images/edits` | no standard shape | no standard shape |
| `AzureOpenAI` (exists) | possible | possible | no | no |
| new vendor kind | only when a specific hosted segmentation/depth vendor is actually chosen | | | |

**Do not invent kinds speculatively.** The `MiniMax` row is the one verified end-to-end path and the
kind already exists; a new kind should be added when there is a signed-up vendor, not before.

### Capability rules in the admin controller

`InferenceProvidersController.IsUnsupportedCombination` is the gate. Its own comment records the trap:

> `// Embedding restrictions. Positive allowlist (AzureOpenAI, OpenAICompatible), because the`
> `// fallthrough of this method is ALLOW and a kind-by-kind denylist would silently admit every`
> `// kind added later.`

So **all four new capabilities must be added as positive allowlists**, and any new provider kind
added later must be added to each list explicitly. The current file already has both patterns side by
side: denylists for the older capabilities (`TypeSafe`->Chat, `Anthropic`->Transcription), positive
allowlists for `Embedding` and `VideoGeneration`. This plan follows the allowlist.

Alongside it, `BuildStatus` needs four new human labels (today: `"voice service"`,
`"speech-to-text service"`, `"image understanding service"`, `"video generation service"`,
`"search indexing service"`, `"decision service"`) or the admin status endpoint will render a
capability with no wording.

**The full edit surface for one capability** - this is the checklist the first work package should
follow, and it is longer than "add an enum member":

1. `ReelBolt.Shared/Data/Models/Enums.cs` - the enum member.
2. `Shared/Inference/IInferenceProviderResolver.cs` + `InferenceProviderResolver.cs` - the
   `Resolve*` method.
3. `Shared/Inference/I*Client.cs` + `*Client.cs` + `I*ClientFactory` + the factory.
4. `Shared/Inference/Resolved*Provider.cs` - the resolved record.
5. `InferenceProvidersController.IsUnsupportedCombination` - the positive allowlist.
6. `InferenceProvidersController.BuildStatus` - the label.
7. `web/lib/types/inference-provider.ts` - the mirrored union (**and its comment block**; the file
   documents each capability's meaning).
8. `web/components/admin/InferenceProviderForm.tsx` + table + their tests.
9. `Shared/Workflows/StepProviderReferences.cs` - if any step config field names a provider of this
   capability.
10. `ResolvedProviderFingerprint` - so the step-cache key changes when the provider changes.
11. `Shared/Metering/` - a `UsageKind`, a rate card, and a decorator in `MeteringMediaClients.cs`.
12. `docs/` + `docs-site/sidebars-developer.js`.

There is a drift guard in the repo (`ToolScopingDriftGuardTests`, `InferenceApiClientDriftGuardTests`,
the schema-drift test in `web/lib/editing`) so any mirrored pair must move together. The web-side
capability union is **not** currently guarded against the C# enum - that is a real gap this plan
should close opportunistically, exactly as `VideoStoryEditorPromptConsistencyTests` guards the
duplicated prompts.

### Where a thumbnail step belongs in the workflow

A new `StepType.ThumbnailGenerate` with its own `IStepExecutor`, following
`VideoCompileStepExecutor`'s shape (deterministic, non-LLM, never throws, always emits valid JSON
because `output_json` is `jsonb`). Concretely:

- **`StepCachePolicy`** - cacheable, the same reasoning as `VideoCompile`: identical inputs produce
  an identical thumbnail, and the whole point is not to pay twice.
- **Retry semantics** - one retry, like the other deterministic types; a hosted image call that
  times out benefits from a retry, and a retry is what re-polls an async job.
- **Provider reference** - `ThumbnailGenerateStepConfig.ImageProviderId`, which means a
  `StepProviderReferences` entry and therefore `StepProviderOwnershipValidator` protection at save
  time (A10) and inclusion in the resolved-provider fingerprint.

---

## The GPU question, said loudly

**Nothing in this document that self-hosts a model can run on our current infrastructure. Our cloud
runs no local model containers by design, and our compute nodes have no GPU.**

The evidence, from the repo rather than from memory:

- an operator file moves `postgres`, `garage`, `whisper` and
  `embeddings` behind a never-activated `cloud-disabled` profile. The file's own header states the
  reason for the model containers: `whisper` -> "hosted transcription provider, no CPU ASR on a
  billable VM"; `embeddings` -> "a CPU TEI server steals engine CPU".
- Hosted providers replace them: the Inference API seeds platform-managed rows from the
  `PlatformProviders` array at startup, wired from `PLATFORM_ASR_*` and `PLATFORM_EMBEDDING_*`
  ([docs/embeddings.md](embeddings.md), an operator file).
- The F0 cloud ADR sizes the launch on **CPU-only** nodes: 8 vCPU, ffmpeg in the engine container,
  Remotion sandboxes at `SANDBOX_CPU_LIMIT=2` / `SANDBOX_MEMORY_LIMIT=2g`
  ([docs/design/cloud-provider-adr.md](design/cloud-provider-adr.md)). **No GPU instance appears
  anywhere in the price comparison.**

So there are exactly three ways to run an open-weight image model, and each must be chosen
deliberately:

| Path | Who pays | Fits current scope? | Cost |
|---|---|---|---|
| **(a) Hosted vendor API** | We do, as metered pass-through | **Yes** - this is the existing pattern | per-image, priced by the vendor |
| **(b) Operator self-host** | The operator (self-host install) | **Yes, for self-host only** - a new optional compose profile behind a GPU requirement | the operator's electricity and hardware |
| **(c) OrgRunner - the user's own machine** | The end user | **Architecturally yes, and this is the interesting one** | zero to us |

**Path (c) deserves a paragraph of its own.** The compute fabric already exists: `runner_devices`,
the pairing flow, the capability probe (`RunnerCapabilityProbe`), the gateway, and a worker binary
(`sandbox/cmd/reelbolt-worker`, D13). A desktop runner that happens to have a GPU could run a
Depth Anything or LaMa container locally, and the engine would reach it through the exact same
`IMediaWorkspace` seam everything else uses. That turns "we cannot afford a GPU" into "the user with
a 4090 gets the 3D features and nobody else does" - which is a defensible product position, and one
that requires **no** platform GPU spend. It is explicitly *not* free to build (the runner's
capability vocabulary would need image-model entries, and the sandbox egress policy is not designed
for pulling model weights), and it is out of scope for the first slice.

**The one exception worth measuring early:** Depth Anything V2 **Small is 24.8M parameters** - three
orders of magnitude below Qwen-Image's 20B. A CPU-feasible depth pass over a single still is
plausible, and it is the single cheapest thing that could make a "3D-aware" demo real. I did **not**
measure its latency and the plan should treat "Depth Anything V2 Small runs fast enough on CPU for a
1080p still" as **unverified**.

---

## Cost

**I could not verify GPU hosting prices in this session.** Hetzner's GPU matrix and RunPod's pricing
page are both client-rendered and returned no price data to a text fetch; the only figure I obtained
is third-party and therefore tagged as such: Hetzner **GEX44 ~ EUR 184/month** from a hosting
aggregator ([whtop.com](https://www.whtop.com/de/amp/plans/hetzner.com/128304)). Treat it as an order
of magnitude, not a quote.

Two things are nevertheless certain enough to plan around:

1. **Hosted image calls are pass-through costs we can meter exactly**, using the machinery that
   already exists. The pattern is settled by `VideoGenerate`: an `external_generation_jobs` spend
   ledger in dollars, plus one idempotent `usage_events` row per unit, priced through
   `IRateCardService` for the credit side. Images fit that shape without inventing anything.
2. **A GPU node is a fixed monthly cost that does not scale to zero**, which is precisely what the
   operator's constraint ("money is tight, avoid large fixed spend until the product pays for itself",
   F0 section 1) forbids. This is the strongest argument for the hosted-first phasing below.

**B13 is the right home for the actual numbers** - it already exists to "set launch credit prices
from measured costs (product decision D10)". This plan should not pre-empt it.

---

## Storage, metering and retention

- **Storage:** outputs go to the existing object store under the project prefix
  (`projects/{projectId}/...`), which is what makes **D2's presigning boundary** authorise a URL and
  keeps a runner-hosted generation inside the same containment as everything else.
- **The index:** one `media_derivatives` row per artefact, with `Kind = Thumbnail`, the storage key,
  bucket, mime type, size, and `Width`/`Height`. The UUID-to-object mapping already exists; the
  feature only has to write to it.
- **Retention is an open decision that must not be forgotten.** `StorageRetentionService` records
  that `media_derivatives` **is not swept** - because it currently has no writer. The moment a writer
  exists, an unswept derivative table becomes unbounded growth on a table we bill storage for
  (B3d samples the `projects/{id}/` prefix by GB-day). The first work package must decide whether a
  thumbnail is delete-on-project-delete (cascade is already declared on `ProjectId` and
  `SourceProjectFileId`) or has its own retention mark.
- **Metering:** a new `UsageKind` per billable unit. `UsageKind` is **append-only, persisted by
  member name**, so a member is added, never renamed. The likely members are one per hosted
  generation/edit/segmentation/depth unit - but note that B3c already records compute as
  `AnalyzeMediaSeconds`/`EncodeOutputSeconds`, so a *locally selected* thumbnail (no model call)
  **costs nothing extra to meter** and should therefore be free, not charged.
- **Billing honesty:** a thumbnail produced from the user's own footage on our own CPU is compute we
  already pay for inside the render. Charging credits for it would be inventing a charge point, which
  is exactly the failure mode B6c had to fix in reverse (a hold that was never reachable). Recommend:
  **charge only for model calls**, metered at the provider boundary.

---

## Phased recommendation

The phasing is ordered so that **each phase ships on its own and the expensive part is last**. It
deliberately front-loads the phase that needs no model at all, because that is the phase that
actually answers "thumbnail generation for videos".

### P0 - this document
Approve or reject the capability design, the licence rules and the phasing. No code.

### P1 - **Thumbnails from the user's own video. No image model. No GPU. ~2-4 sessions.**
The first slice, and it is small because the hard parts already exist.

Scope:
- `StepType.ThumbnailGenerate` + `ThumbnailGenerateStepExecutor`, deterministic and non-LLM,
  modelled on `VideoCompileStepExecutor`.
- Candidate selection reuses **`VideoAnalyze`'s existing output**: shot boundaries, the frame grid,
  `FrameGridAnalyzer.ComputeTakeQuality`, sharpness, duplicate grouping. Prefer the first frame of a
  shot that is not a duplicate and scores well on sharpness - the parameters are already computed and
  currently only used for edit decisions.
- Extract candidates with the existing `IKeyframeExtractor.ExtractKeyframeAsync` at the project's
  thumbnail width, over `IMediaWorkspace` so it runs on a runner when the fabric is on.
- Compose the final image (crop/aspect + optional text overlay) deterministically. **Text overlay is
  the one part with a real choice**: an ffmpeg `drawtext` overlay, or a tiny Remotion composition.
  Recommend Remotion, because the sandbox render path, the output verification and the metering
  already exist for it.
- Publish through `PublishAsync` -> `IOutputVerifier` -> a `media_derivatives` row
  (`Kind = Thumbnail`).
- `Auto` / `AtTime` config; a thumbnail never calls an inference provider in P1.
- Retention decision taken here (see above).

**Why this first:** it delivers the headline feature, it proves the `media_derivatives` writer and
the derivative storage route, it needs no new capability, no new kind, no GPU, no licence review and
no metering change - and everything afterwards builds on the same plumbing.

### P2 - **The image-model arm: one new capability, one hosted vendor. ~2-3 sessions.**
- Add `InferenceProviderCapability.ImageGeneration` (positive allowlist; `MiniMax` +
  `OpenAICompatible`).
- A MiniMax image arm on the **existing** `MiniMax` kind over
  `POST https://api.minimax.io/v1/image_generation`, model `image-01`, `response_format: url`,
  optional `subject_reference`.
- Admin UI, `BuildStatus` label, web capability union, metering decorator + `UsageKind` + rate card.
- A `ThumbnailGenerate` config switch: `Source = Frame | Generated`, where `Generated` seeds a
  cover from the chosen frame plus a prompt (image-to-image) rather than from text alone - the frame
  is nearly always a better starting point than an empty canvas.
- Gate the whole thing behind the provider being configured, exactly as Voiceover does: **no provider
  row means the step falls back to the deterministic frame and says so**, never a failure.

**Why second:** it is the smallest possible first contact with a hosted image model - one kind that
already exists, one endpoint, no licence decision, no GPU - and it validates the metering and admin
surface before the capability count grows.

### P3 - **The image editor v1: subjects -> layers. 2D, honestly labelled. ~5-8 sessions.**
- Add `Segmentation` (+ `ImageEdit`) capabilities.
- An **image document** alongside `EditTimeline` (v2 is a *time*-based document; an image editor needs
  a *layer* document: ordered RGBA layers, a per-layer transform, an opacity, a z-order). Do **not**
  overload `EditTimeline`: `docs/editor.md` is explicit that the v2 schema pins
  `schemaVersion: 2` and that the TypeScript mirror is hand-written and drift-tested.
- Select a subject via `IObjectLocator` (boxes, exists today) to seed a segmentation call, then cut
  an alpha layer.
- Layer ops: move, scale, recolour, delete, reorder, opacity - the operation set Qwen-Image-Layered
  demonstrates.
- Repair the hole left behind with `ImageEdit`/inpainting.
- **This phase is 2D and the UI must not claim otherwise.**

### P4 - **Depth and 2.5D. Gated on a GPU decision. Not scheduled.**
- Add the `Depth` capability; a 2.5D parallax demo from a still via layered-depth inpainting.
- **Gate:** the owner decides between (a) a hosted depth vendor, (b) funding a GPU node, or (c) the
  OrgRunner path. Until then this phase is a design, not a commitment.

### P5 - **Camera-controlled video. Explicitly out of scope.**
Requires a GPU-hosted video diffusion model with pose conditioning. Recorded here so the owner can
see it was considered and priced, not forgotten. **Do not promise this.**

---

## What could not be verified

Listed plainly, because an unsourced claim about a model is worse than an admitted gap:

1. **Higgsfield's actual technique.** Their blog and help centre are client-rendered and returned no
   content; their API docs document generation models only and no camera-control or 3D endpoint. The
   "45+ camera presets" figure is from third-party press. **I could not confirm how Higgsfield
   produces the effect the owner is describing.**
2. **TrajectoryCrafter's licence.** No `LICENSE` file at the repository root (404 on both
   `LICENSE` and `LICENSE.txt`). **Unlicensed code cannot be used.**
3. **GEN3C's effective licence.** The *code* is Apache-2.0, but it runs on **Cosmos** weights, whose
   licence I did not read. Apache-2.0 on the wrapper does not license the weights.
4. **Depth Anything V2 Small's CPU latency** for a 1080p still. The parameter count (24.8M) makes it
   plausible; I did not measure it.
5. **GPU hosting prices.** Hetzner and RunPod pricing pages did not render to text. No GPU figure in
   this document is reliable; the GEX44 number is third-party.
6. **Qwen-Image-2.0's licence** (the 2026-02-10 release). The parent model is Apache-2.0; I did not
   read the successor's card.
7. **HiDream-E1.1's licence** (the editing sibling of the MIT-licensed HiDream-I1).
8. **Uni3C's licence and code availability.**
9. **Whether any hosted provider offers mask-based inpainting as a first-class API** (as opposed to
   instruction-only editing). This decides whether P3's hole-repair step is a hosted call or an
   operator-hosted LaMa.
10. **Stability AI - nothing at all.** Pricing, whether SD3.5/SDXL are hosted, and the API-vs-weights
    licence comparison are all unverified: `platform.stability.ai` is a client-rendered SPA behind
    Cloudflare and every path (`/pricing`, `/llms.txt`, `/openapi.json`) returned the same 1,644-byte
    shell.
11. **Per-image prices for Google Gemini, Ideogram and MiniMax.** The pricing pages either sit past
    what a text fetch returns (Gemini) or are JS/Cloudflare-gated (Ideogram). MiniMax image pricing
    was not fetched at all.
12. **Commercial terms for OpenAI, fal.ai, Replicate, Recraft and Ideogram** - their terms pages were
    not fetchable. **Only BFL's terms were read in full**, which is why BFL is the only hosted vendor
    this document makes a licence statement about.
13. **Replicate's webhook mechanics** (URL and signature details) - the docs page renders nav-only.
14. **Whether OpenAI or Recraft offer an async/background image mode** - not documented on the pages
    fetched.

---

## Hosted image APIs: the fetched comparison

Every row below was read from the vendor's own documentation during this session. Where a
figure could not be obtained the row says so; **none of the prices here are estimates**.

| Provider | Capability | Endpoint(s) | Auth | Price | Notes |
|---|---|---|---|---|---|
| **MiniMax** (kind already in our code) | generate + reference-image edit | `POST https://api.minimax.io/v1/image_generation`, model `image-01` | `Authorization: Bearer` | **not verified in this session** | the only vendor here we already integrate; see [above](#minimax-is-the-standout-because-we-already-integrate-it) |
| **Black Forest Labs** | generate + edit + **mask inpaint** | `POST https://api.bfl.ai/v1/flux-3-image`; `/v1/flux-2-{max,pro,flex,klein-9b,klein-4b}`; legacy `/v1/flux-kontext-{pro,max}`; `/v1/flux-pro-1.0-fill` (inpaint w/ mask), expand, erase, deblur | `x-key` | 1 credit = $0.01. FLUX 3 Image: 768sq $0.041, 1k $0.048, 2k $0.100, 4k $0.607. FLUX.2 pro $0.03 t2i / $0.045 edit; max $0.07; klein 4B $0.014. Kontext pro $0.04 / max $0.08. **Fill [pro] inpaint $0.05** ([pricing](https://docs.bfl.ml/quick_start/pricing.md)) | **async**: submit -> `{id, polling_url}`; webhooks with `webhook_secret`. Result URLs **expire in 10 minutes** and are not CORS-enabled -> we must download and re-serve. **Read the licence note below.** |
| **fal.ai** | generate + edit | `POST https://queue.fal.run/{model-id}` | `Authorization: Key` | per model, per image **or** per megapixel: Nano Banana 2 $0.08/img, NB Pro $0.15/img, Flux 3 Image $0.024/megapixel, Z-Image Turbo $0.005/megapixel ([pricing](https://fal.ai/pricing)) | **async**: queue -> `{request_id, status_url, response_url, cancel_url}`; webhooks with signature verification, 15s initial / 120s retry ([queue](https://fal.ai/docs/documentation/model-apis/inference/queue.md), [webhooks](https://fal.ai/docs/documentation/model-apis/inference/webhooks.md)) |
| **OpenAI** | generate + edit/inpaint | `POST /v1/images/generations`, `POST /v1/images/edits` (multipart `image[]` + optional `mask`) | `Authorization: Bearer` | **per 1M tokens, not per image** — e.g. `gpt-image-2` image input $8.00 / cached $2.00 / output $30.00; batch tier is half ([pricing](https://developers.openai.com/api/docs/pricing.md)) | **synchronous** — the only sync provider here; guide warns of up to 2 minutes. Mask is *prompt guidance*, "not pixel-exact" ([guide](https://developers.openai.com/api/docs/guides/image-generation.md)) |
| **Google Gemini** ("Nano Banana") | generate + conversational edit | `POST https://generativelanguage.googleapis.com/v1beta/interactions` | `x-goog-api-key` | **per-image price could not be verified** ([pricing](https://ai.google.dev/gemini-api/docs/pricing)) | **Imagen is retired** — the docs say "Imagen models are discontinued... use Nano Banana" ([docs](https://ai.google.dev/gemini-api/docs/imagen)). Up to 14 reference images; **all outputs carry a SynthID watermark** |
| **Ideogram** | generate + Precise Edit / remix / inpaint | `POST https://api.ideogram.ai/v2/image/generate/{model}` | `Api-Key` | **per-image price could not be verified** — page is JS/Cloudflare-gated | **async** with **Ed25519-signed** webhooks. Notable for **text rendering**, and it has a `Layerize` endpoint ([docs](https://developer.ideogram.ai/)) |
| **Recraft** | generate + inpaint + outpaint + erase + remove-bg | `https://external.api.recraft.ai/v1` — `/images/generations`, `/images/inpaint`, `/images/eraseRegion`, ... | `Authorization: Bearer` | $1 = 1,000 units. V4.1 Flash **$0.007/image**; V3 imageToImage/inpaint/outpaint **$0.04 raster**; erase $0.002 ([pricing](https://www.recraft.ai/docs/api-reference/pricing)) | the **cheapest verified edit path** by a wide margin |
| **Replicate** | generate + edit (model-dependent) | per model | API token | flux-1.1-pro $0.04/output image, flux-dev $0.025, ideogram-v3-quality $0.09 ([pricing](https://replicate.com/pricing)) | webhooks documented, mechanics unverified |
| **Stability AI** | **could not verify anything** | — | — | — | `platform.stability.ai` is a client-rendered SPA behind Cloudflare; every path returned the same 1,644-byte shell |

### The finding that matters most: BFL's API terms are not just a price

I fetched and read the [FLUX API Service Terms](https://bfl.ai/legal/flux-api-service-terms)
directly, and two clauses should change how we think about FLUX entirely.

**1. The hosting clause is narrower than it looks, and we are probably inside it.** The grant is to
*"develop and operate integrations whereby End Users of a Developer Application can interface with
the FLUX AI Models **from within** the Developer Application"*, and the prohibition is on hosting
*"an API endpoint to any FLUX AI Models that allows third parties to integrate or otherwise use the
FLUX AI Models **in or with their own products or services**"*. A platform-managed provider row that
ReelBolt calls on a user's behalf is the permitted case. A **BYO** provider row is where this gets
interesting — but a customer pointing their own BYO row at their own key is not us hosting a FLUX
endpoint either. Flagging it rather than resolving it: this is a legal question, not an engineering
one.

**2. The training clause is a genuine data-protection problem.** The terms say the developer grants
BFL *"a fully paid, royalty-free, perpetual, irrevocable, worldwide, non-exclusive, and fully
sublicensable right and license to use, sub-license, distribute, reproduce, modify, adapt, publicly
perform, and publicly display Developer's Input **and Output**"*, and explicitly: *"the Company may
use Inputs and Outputs to train and improve its artificial intelligence models."*

For a video platform, **Input** means a frame of the customer's footage. That is sending a
customer's material to a vendor who will train on it, on terms that are irrevocable and
sublicensable, with no opt-out stated in the terms I read. The operator sells
to EU customers. **This should be treated as a blocker for any FLUX row serving customer footage,
independent of price, until someone reads the DPAs.** It is exactly the kind of finding this
document exists to surface before a work package is scoped — and it is a strong argument for
preferring the Apache-2.0 and MIT models we can genuinely control, or a hosted vendor whose terms do
not claim a training licence over inputs.

**Note the flip side too:** BFL's own pricing page lists `FLUX.2 [dev]` as *"Local only — open
weights, non-commercial (no hosted API)"* and sells separate **Open Weights Licensing** tiers
(Builder 10K images/mo, Platform 100K, Professional 100K/3 domains, Enterprise, Synthetic Data —
[bfl.ai/pricing](https://bfl.ai/pricing)). So "FLUX commercially" is purchasable; it is simply a
paid licence, not a free one.

### Async versus sync, and why it matters to us

Everything except OpenAI hands back a **job id plus a webhook**:

- BFL: `{id, polling_url}`, plus `webhook_url` / `webhook_secret`
- fal.ai: queue submit -> `{request_id, status_url, response_url, cancel_url}`, plus `fal_webhook`
- Ideogram: `generation_id` + Ed25519-signed webhooks
- Gemini: Interactions API `background: true` -> interaction id, plus a Webhooks API

**That is the same shape `MiniMaxVideoGenerationClient` already implements** — submit, poll, and
treat a retry as the thing that re-polls a timed-out job. So a new hosted image arm should reuse that
poll/webhook pattern rather than invent a second one, and the OpenAI arm is the awkward outlier: it
holds a synchronous connection for up to two minutes, which is a poor fit for a workflow step and an
argument for making OpenAI a *second* integration rather than the first.

### What this changes in the plan

The concrete recommendation from this survey: **for P2, the first hosted image arm should be MiniMax**
(the kind already exists, no new licence review, no new auth style) and **the second should be
Recraft or fal.ai** (cheap, verified per-image pricing, async + webhooks).
**Black Forest Labs should not be integrated until the training clause in its API terms is assessed
by someone qualified to assess it.**

---

## Sources

Licences (all fetched from the canonical licence file or model card):

- FLUX [dev] Non-Commercial License v2.0 - <https://bfl.ai/legal/non-commercial-license-terms>
- FLUX.1-schnell (Apache-2.0) - <https://huggingface.co/black-forest-labs/FLUX.1-schnell>
- Qwen-Image-Edit-2511 (Apache-2.0) - <https://huggingface.co/Qwen/Qwen-Image-Edit-2511>
- Qwen-Image-Layered (Apache-2.0) - <https://huggingface.co/Qwen/Qwen-Image-Layered>
- Qwen-Image repository - <https://raw.githubusercontent.com/QwenLM/Qwen-Image/main/README.md>
- Z-Image-Turbo (Apache-2.0) - <https://huggingface.co/Tongyi-MAI/Z-Image-Turbo>
- Lumina-Image 2.0 (Apache-2.0) - <https://huggingface.co/Alpha-VLLM/Lumina-Image-2.0>
- HiDream-I1 (MIT) - <https://huggingface.co/HiDream-ai/HiDream-I1-Full>
- SDXL base 1.0 (openrail++) - <https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0>
- Stable Diffusion 3.5 Large - <https://huggingface.co/stabilityai/stable-diffusion-3.5-large>
- Wan 2.2 (Apache-2.0) - <https://huggingface.co/Wan-AI/Wan2.2-T2V-A14B>
- LaMa (Apache-2.0) - <https://github.com/advimman/lama/blob/main/LICENSE>
- IC-Light (Apache-2.0) - <https://github.com/lllyasviel/IC-Light/blob/main/LICENSE>
- SAM 2 (Apache-2.0) - <https://github.com/facebookresearch/sam2/blob/main/LICENSE>
- SAM 3 (SAM License) - <https://github.com/facebookresearch/sam3/blob/main/LICENSE> -
  <https://scancode-licensedb.aboutcode.org/sam-2025-11-19.html>
- Grounding DINO (Apache-2.0) - <https://github.com/IDEA-Research/GroundingDINO/blob/main/LICENSE>
- BiRefNet (MIT) - <https://huggingface.co/ZhengPeng7/BiRefNet>
- RMBG-2.0 (bria-rmbg-2.0) - <https://huggingface.co/briaai/RMBG-2.0>
- Depth Anything V2 (Apache-2.0) - <https://github.com/DepthAnything/Depth-Anything-V2/blob/main/LICENSE> -
  <https://huggingface.co/depth-anything/Depth-Anything-V2-Small>
- Video Depth Anything (Apache-2.0) - <https://github.com/DepthAnything/Video-Depth-Anything/blob/main/LICENSE>
- Depth Pro (apple-amlr) - <https://huggingface.co/apple/DepthPro>
- TRELLIS (MIT) - <https://github.com/microsoft/TRELLIS/blob/main/LICENSE>
- 3D Photo Inpainting (MIT) - <https://github.com/vt-vl-lab/3d-photo-inpainting/blob/main/LICENSE>
- SAM 3D - <https://ai.meta.com/blog/sam-3d/>
- ReCamMaster (MIT) - <https://github.com/KwaiVGI/ReCamMaster>
- GEN3C (Apache-2.0) - <https://github.com/nv-tlabs/GEN3C/blob/main/LICENSE>
- TrajectoryCrafter - <https://github.com/TrajectoryCrafter/TrajectoryCrafter> (no licence found)

Technique and capability:

- GEN3C project page (3D cache, depth unprojection, camera-conditioned diffusion) -
  <https://research.nvidia.com/labs/toronto-ai/GEN3C/>
- ReCamMaster (camera trajectories) - <https://github.com/KwaiVGI/ReCamMaster>
- TrajectoryCrafter (novel views, >= 28 GB VRAM) - <https://github.com/TrajectoryCrafter/TrajectoryCrafter>
- MiniMax image generation (text-to-image, `subject_reference`) -
  <https://platform.minimax.io/docs/guides/image-generation>
- MiniMax text-to-image API reference - <https://platform.minimax.io/docs/api-reference/image-generation-t2i>
- MiniMax video generation (reference camera/motion; bracketed camera directives) -
  <https://platform.minimax.io/docs/guides/video-generation> -
  <https://platform.minimax.io/docs/api-reference/video-generation-t2v>
- Higgsfield model catalog (generation only) - <https://docs.higgsfield.ai/docs/models>
- Higgsfield camera presets (third-party) - <https://cgworld.jp/flashnews/01-202504-Higgsfield-AI.html>
- Higgsfield Blender camera plugin (third-party) -
  <https://www.ai-primer.com/creative/stories/higgsfield-blender-camera-control>

Repo, for the architecture section:

- [docs/embeddings.md](embeddings.md) - the precedent for adding a capability
- [docs/voiceover.md](voiceover.md) - the Fish Audio hosted-vs-weights licence precedent
- [docs/video-generation.md](video-generation.md) - the `VideoGenerate` step, spend ledger and metering
- [docs/video-editing.md](video-editing.md) - `IMediaWorkspace`, the extraction phase, vision captioning
- [docs/editor.md](editor.md) - the v2 timeline document and the `media_derivatives` "no writer yet" note
- [docs/compute-fabric.md](compute-fabric.md) - the runner, the capability probe and the session seam
- [docs/design/cloud-provider-adr.md](design/cloud-provider-adr.md) - the CPU-only launch topology

Hosted provider documentation (read directly from the vendor's own pages):

- OpenAI: [image generation guide](https://developers.openai.com/api/docs/guides/image-generation.md) -
  [Images API reference](https://developers.openai.com/api/reference/resources/images.md) -
  [pricing](https://developers.openai.com/api/docs/pricing.md)
- Google Gemini: [image generation](https://ai.google.dev/gemini-api/docs/image-generation) -
  [Nano Banana 2.1 model page](https://ai.google.dev/gemini-api/docs/models/gemini-nano-banana-2.1) -
  [Imagen discontinued](https://ai.google.dev/gemini-api/docs/imagen) -
  [background execution](https://ai.google.dev/gemini-api/docs/background-execution) -
  [pricing](https://ai.google.dev/gemini-api/docs/pricing)
- Black Forest Labs: **[FLUX API Service Terms](https://bfl.ai/legal/flux-api-service-terms)** (the
  training and hosting clauses) - [pricing](https://docs.bfl.ml/quick_start/pricing.md) -
  [OpenAPI spec](https://api.bfl.ai/openapi.json) - [open-weights licensing](https://bfl.ai/pricing)
- fal.ai: [pricing](https://fal.ai/pricing) -
  [queue API](https://fal.ai/docs/documentation/model-apis/inference/queue.md) -
  [webhooks](https://fal.ai/docs/documentation/model-apis/inference/webhooks.md) -
  [authentication](https://fal.ai/docs/documentation/model-apis/authentication.md)
- Replicate: [pricing](https://replicate.com/pricing) - [docs index](https://replicate.com/docs/llms.txt)
- Ideogram: [developer docs](https://developer.ideogram.ai/) -
  [webhooks](https://developer.ideogram.ai/ideogram-api/webhooks.md) -
  [API setup](https://developer.ideogram.ai/ideogram-api/api-setup.md)
- Recraft: [API endpoints](https://www.recraft.ai/docs/api-reference/endpoints.md) -
  [API pricing](https://www.recraft.ai/docs/api-reference/pricing)
- Stability AI: attempted at <https://platform.stability.ai/pricing>, `/llms.txt`, `/openapi.json` -
  **all returned a 1,644-byte client-rendered shell; nothing verified**

