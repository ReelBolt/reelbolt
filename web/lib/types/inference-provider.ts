import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import type { UsageKind } from './pricing';

/**
 * The kind of backend a provider row talks to. Mirrors the backend `InferenceProviderKind` enum
 * (ReelBolt.Shared/Data/Models/Enums.cs) exactly.
 *
 * `Anthropic` targets Claude through Anthropic's first-party Messages API. It supports the `Chat`
 * and `Vision` capabilities only — Anthropic exposes no speech-to-text API, so the backend rejects
 * an `Anthropic` + `Transcription` row with a 400. See docs/anthropic-provider.md.
 *
 * `Gemini` targets Google Gemini via Google's official OpenAI compatibility endpoint. It supports
 * `Chat` and `Vision` only — Gemini exposes no speech-to-text API. See docs/gemini-provider.md.
 *
 * `DeepSeek` targets DeepSeek via DeepSeek's official OpenAI compatibility endpoint. It supports
 * `Chat` and `Vision` only — DeepSeek exposes no speech-to-text API. See docs/deepseek-provider.md.
 *
 * `TypeSafe` targets TypeSafe via their decision-gate API endpoint. It supports the `Decision`
 * capability only — TypeSafe is specialized for probabilistic decision modeling and logprob-based
 * gate features. See docs/decision-models.md.
 *
 * `FishAudio` targets Fish Audio's hosted speech synthesis API. It supports the `SpeechSynthesis`
 * capability only — Fish Audio is specialized for text-to-speech synthesis and voiceover
 * generation. See docs/voiceover.md.
 *
 * `BlackForestLabs` targets Black Forest Labs (the FLUX vendor) at `https://api.bfl.ai`. It supports
 * `ImageGeneration` and `ImageEdit` only. It is a kind of its own rather than an
 * `OpenAICompatible` row because a base-URL swap cannot reach it: authentication is an `x-key`
 * header rather than `Authorization: Bearer`, the model is named in the URL path rather than in
 * the request body, and a submit is asynchronous — it answers `{id, polling_url}` and the result is
 * polled. Its result URLs expire after 10 minutes and are not CORS-enabled, so a generated image
 * is fetched and re-hosted rather than linked.
 *
 * `FalAi` targets fal.ai's queue API at `https://queue.fal.run`. It supports `Segmentation`, `Depth`,
 * `VideoGeneration` and the image capabilities (the image endpoints are chosen per operation by the
 * backend's `FalAiImageClient.Catalog`, so one `ImageEdit` row serves every editing tool, and a video
 * take names its endpoint through the VideoGenerate step's `Model` — the backend's
 * `FalAiVideoCatalog`; see docs/video-generation.md "Set up a fal.ai provider"). It is a kind of its own rather than an `OpenAICompatible` row because fal is not an
 * OpenAI-shaped API at all: a request is queued and polled, the model is named as a path segment
 * (`fal-ai/evf-sam`) rather than in the request body, and the answer is a JSON envelope carrying
 * URLs to the produced PNGs.
 *
 * `Higgsfield` targets Higgsfield at `https://api.higgsfield.ai`. It supports the `VideoGeneration`
 * capability only — the backend rejects any other capability for this kind with a 400. Its
 * credential is deliberately NOT a single opaque token: it is a two-part `ID:SECRET` string (the
 * Key ID and Key Secret joined by a colon), sent as `Authorization: Key <ID>:<SECRET>`. See
 * docs/video-generation.md.
 *
 * `ComfyUI` targets a SELF-HOSTED ComfyUI server (e.g. the optional `comfyui` compose service under
 * the `gpu` profile, `http://comfyui:8188`). It supports the `VideoGeneration` capability only, bills
 * nothing (a VideoGenerate step budgets it in GPU minutes instead of USD), and only ever submits the
 * two vetted first-party Wan 2.2 workflow templates — never a free-form graph. Its key is an optional
 * bearer token for a reverse proxy; blank sends no Authorization header. See docs/video-generation.md.
 * Both appended last, like every other member.
 *
 * `LocalRunner` is a model served by the user's own computer through the ReelBolt desktop app (the
 * runner). Workspace providers only; its endpoint is the paired device's id, there is no API key, and
 * it serves `Chat`, `Vision` and `Embedding` only. No credits are used.
 *
 * `ElevenLabs` is DORMANT (product decision: music and sound effects go through fal only): the member stays
 * (append-only) but the backend refuses it every capability and the admin form does not offer it. Appended
 * last. See docs/elevenlabs-provider.md.
 */
export type InferenceProviderKind = 'AzureOpenAI' | 'OpenAICompatible' | 'Anthropic' | 'Gemini' | 'DeepSeek' | 'TypeSafe' | 'MiniMax' | 'FishAudio' | 'BlackForestLabs' | 'FalAi' | 'Higgsfield' | 'ComfyUI' | 'ElevenLabs' | 'LocalRunner';

/**
 * What an InferenceProvider row can be used for. Mirrors the backend `InferenceProviderCapability`
 * enum (ReelBolt.Shared/Data/Models/Enums.cs) and `InferenceProviderResponse.Capability`
 * (Inference.Api/Controllers/Dto/InferenceProviderDtos.cs) exactly. `Chat`, `Transcription`,
 * `Vision` (Phase 2 of video editing — shot captioning, see docs/video-editing.md), `Decision`,
 * `SpeechSynthesis`, `ImageGeneration` and `ImageEdit` each participate in their own independent
 * "at most one default" constraint, so a chat-provider picker (e.g. the per-agent override) must
 * filter to `Chat` only — never merely "not Transcription", which would incorrectly admit
 * `Vision`/`Decision`/`SpeechSynthesis`/`ImageGeneration`/`ImageEdit` rows too. `Decision` providers
 * serve probabilistic decision-gate features using logprob-based access to TypeSafe's Jev model.
 * `SpeechSynthesis` providers serve text-to-speech generation for voiceover synthesis. See
 * docs/voiceover.md.
 */
/**
 * `Embedding` serves semantic file search (text embeddings; Azure OpenAI or an OpenAI-compatible
 * server such as text-embeddings-inference). Anthropic/DeepSeek/Gemini cannot serve it.
 */
/**
 * `ImageGeneration` (text, plus an optional reference image, to an image) and `ImageEdit` (an image
 * plus an instruction and/or a mask, to an image) are capabilities of their own — never modes of
 * `Chat`, `Vision` or `VideoGeneration`, and never interchangeable with them: a chat or vision row
 * reads an image and answers in text, a video-generation row produces moving pictures, and none of
 * the three can make a still. They are not interchangeable with each other either, which is why
 * they are two members and not one: `ImageGeneration` starts from an empty canvas and a prompt,
 * `ImageEdit` starts from pixels that already exist. A vendor usually serves both, so the same kind
 * appears against both capabilities — but they are two rows with two independent "at most one
 * default" constraints, so an operator can generate on one vendor's row and edit on another's.
 */
/**
 * `Segmentation` (a picture plus a prompt, to an RGBA cut-out of one subject in it) and `Depth` (a
 * picture, to a depth map of it) are two more capabilities of their own. The first is what turns a
 * whole image into a *layer* in the image editor — a cut-out with alpha, not a copy of the frame —
 * and the second describes how far away each part of a picture is. Neither is a mode of `Vision`:
 * a vision row reads an image and answers in prose, and neither of these reads prose at all. Only
 * `FalAi` serves them; every other kind is refused for both.
 */
/**
 * The six picture-editor job capabilities (`ImageErase` … `ImageUpscale`) were split out of `ImageEdit`
 * so each AI tool resolves a provider and model built for it; `ImageEdit` now means the instruction
 * edit ("make the sky purple"). See `lib/inference/image-capabilities.ts`.
 */
export type InferenceProviderCapability =
  | 'Chat'
  | 'Transcription'
  | 'Vision'
  | 'Decision'
  | 'VideoGeneration'
  | 'SpeechSynthesis'
  | 'Embedding'
  | 'ImageGeneration'
  | 'ImageEdit'
  | 'Segmentation'
  | 'Depth'
  | 'ImageErase'
  | 'ImageFill'
  | 'ImageExpand'
  | 'ImageBackgroundReplace'
  | 'ImageRelight'
  | 'ImageUpscale'
  /**
   * The platform's own render, encode and media-analysis work, metered against the system compute
   * provider (`isSystem`). Appended last, like every member. It is never offered in a create form,
   * an add-capability list or a picker: no admin can create one and no step chooses one.
   */
  | 'Compute'
  /**
   * Music and sound-effect generation (fal.ai's hosted ElevenLabs/MMAudio endpoints, or `ElevenLabs` directly).
   * Appended after `Compute`, like every member. The admin forms that offer them come with the audio UI.
   */
  | 'MusicGeneration'
  | 'SoundEffectGeneration'
  /**
   * A video plus a text prompt (optionally refined by a point or a box on one frame) to a per-frame
   * mask of that object: "which pixels are the speedometer, in every frame". Resolved by the
   * VideoAnalyze step's `segmentationProviderId` and served by `FalAi` only (SAM 3.1 video-rle by
   * default, billed per 16 frames of video input).
   *
   * Its own capability, not a video-flavoured `Segmentation` (which reads ONE picture): a video mask
   * has to stay consistent from frame to frame and is priced per frame, and the analyze step resolves
   * it independently of the picture editor's rows.
   *
   * APPENDED LAST, matching `InferenceProviderCapability` in `Enums.cs`: the enum is persisted by
   * NAME, so insert order is not load-bearing, but the house rule is append-only.
   */
  | 'VideoSegmentation'
  /**
   * Content moderation (OpenAI's moderation endpoint, reached through an
   * `OpenAICompatible` row only). Video and audio are sampled into derived frames plus text before
   * the call; the model accepts text and images, never audio or video. An unresolved capability
   * means moderation is off, never a fallback to a chat deployment. Appended last, like every
   * member.
   */
  | 'ContentModeration'
  /**
   * Relevance reranking for the assistant's documentation search (`POST {endpoint}/rerank`). Served by an
   * `OpenAICompatible` row only; an unresolved or unpriced provider means the search keeps its built-in
   * order. Appended last, like every member.
   */
  | 'Rerank'
  /**
   * Live web lookups for workflow agents. Served by DeepSeek, Gemini, and an `OpenAICompatible` row only
   * when its endpoint host is api.openai.com (the server enforces this). Billed per search. Appended last.
   */
  | 'WebSearch'
  /**
   * Voice isolation (background noise and music removed from a recording). Served by `FalAi` only
   * (fal-ai/elevenlabs/audio-isolation), billed per minute of source audio. Appended last.
   */
  | 'AudioIsolation'
  /**
   * Hosted video upscaling: one clip in, the same clip at a larger short side. Served by `FalAi` only
   * (fal-ai/topaz/upscale/video by default, fal-ai/bytedance-upscaler/upscale/video), billed per output
   * second of video by resolution tier. Its own capability, not a mode of `ImageUpscale`: the unit and
   * the vendor fields differ. Appended last, like every member.
   */
  | 'VideoUpscale'
  /**
   * Hosted frame interpolation: one clip in, the same clip at a higher frame rate. Served by `FalAi`
   * only (fal-ai/rife/video by default, fal-ai/film/video), billed per compute second. Its own
   * capability, not a mode of `VideoUpscale`. Appended last, like every member.
   */
  | 'FrameInterpolation'
  | 'VideoOutpaint'
  /**
   * Talking-head lip-sync (a still portrait speaking a recording): fal.ai only, the model must be a talking-head
   * endpoint. Its own capability, not a mode of `VideoGeneration`. Appended last, like every member.
   */
  | 'LipSync';

/** Every capability an admin may choose: the whole union except the system-only `Compute`. */
export type SelectableCapability = Exclude<InferenceProviderCapability, 'Compute'>;

/** Where a capability's price comes from: the admin price table, or the vendor's own oracle. */
export type PriceSource = 'Table' | 'Oracle';

/**
 * One price row of a model capability (platform admin only; org responses carry `prices: null`).
 * Mirrors the backend `PriceResponse`. `unit` is a `UsageKind` name. A price is never edited in
 * place: a new row for the same (unit, variant) closes the open one, and Retire ends a row.
 */
export interface ModelCapabilityPrice {
  id: string;
  unit: UsageKind;
  /** Distinguishes prices of one unit inside one capability (a video resolution, a fal tier). */
  variant: string | null;
  /** Our provider cost in USD per ONE unit (one token, one second, one image). */
  costUsdPerUnit: number;
  /** Millicredits per unit set by hand, or null to derive credits from cost × markup ÷ credit value. */
  creditsMillisPerUnitOverride: number | null;
  /** What a unit is actually charged at, in millicredits (1,000 = 1 credit). */
  effectiveCreditsMillisPerUnit: number;
  /** The margin that charge leaves over cost, in percent. */
  effectiveMarginPercent: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  notes: string | null;
}

/** One capability of a model (Chat, Vision, …). Its id is what every picker and step config stores. */
export interface ModelCapability {
  id: string;
  capability: InferenceProviderCapability;
  isDefault: boolean;
  isEnabled: boolean;
  priceSource: PriceSource;
  lastTestAt: string | null;
  lastTestOk: boolean | null;
  lastTestError: string | null;
  /** Platform responses only; always null on the org endpoints. */
  prices: ModelCapabilityPrice[] | null;
}

/** One model under a provider account: the vendor's code and the name customers see. */
export interface InferenceModel {
  id: string;
  modelCode: string;
  displayName: string;
  isEnabled: boolean;
  capabilities: ModelCapability[];
}

/**
 * One provider ACCOUNT (backend `ProviderResponse`): kind, endpoint, key and timeout, shared by every
 * model under it. Changing the key or the endpoint changes every model listed in `models`.
 */
export interface InferenceProvider {
  id: string;
  /** Null for a platform provider, the workspace's id for a bring-your-own one. */
  organizationId: string | null;
  name: string;
  kind: InferenceProviderKind;
  endpoint: string;
  hasApiKey: boolean;
  apiKeyLastFour: string | null;
  isEnabled: boolean;
  /** The platform compute provider: shown, never edited or deleted (the API answers 409). */
  isSystem: boolean;
  timeoutSeconds: number | null;
  /**
   * "Dev/OAuth (Personal use only)": this account talks to Anthropic through a personal Claude
   * subscription credential rather than a Console API key. Only meaningful when `kind` is
   * 'Anthropic'. See docs/anthropic-provider.md.
   */
  personalOAuthMode: boolean;
  createdAt: string;
  updatedAt: string;
  models: InferenceModel[];
}

/** A capability as it is created, on its own or inside a new model. */
export interface CreateModelCapabilityRequest {
  capability: InferenceProviderCapability;
  isDefault: boolean;
  isEnabled: boolean;
}

/** `POST /{id}/models`, and each entry of a new provider's optional `models`. */
export interface CreateInferenceModelRequest {
  modelCode: string;
  displayName: string;
  isEnabled: boolean;
  capabilities: CreateModelCapabilityRequest[];
}

/** `PUT /{id}/models/{modelId}`: omitted fields are left unchanged. */
export interface UpdateInferenceModelRequest {
  modelCode?: string;
  displayName?: string;
  isEnabled?: boolean;
}

/** `PUT /{id}/models/{modelId}/capabilities/{capId}`. `isDefault: true` clears the others. */
export interface UpdateModelCapabilityRequest {
  isDefault?: boolean;
  isEnabled?: boolean;
}

/** `POST /` — a provider account, optionally with its first models. */
export interface CreateInferenceProviderRequest {
  name: string;
  kind: InferenceProviderKind;
  endpoint: string;
  apiKey?: string;
  timeoutSeconds?: number | null;
  isEnabled: boolean;
  /** Only accepted when `kind` is 'Anthropic', and never on a workspace provider. */
  personalOAuthMode: boolean;
  models?: CreateInferenceModelRequest[];
}

/**
 * `PUT /{id}`. The kind is fixed at creation. `apiKey` has three meanings: omitted keeps the stored
 * key, a value replaces it, and `""` clears it.
 */
export interface UpdateInferenceProviderRequest {
  name?: string;
  endpoint?: string;
  apiKey?: string;
  timeoutSeconds?: number | null;
  isEnabled?: boolean;
  personalOAuthMode?: boolean;
}

/** `POST /test` — tests an unsaved configuration; `providerId` reuses that provider's saved key. */
export interface TestInferenceProviderRequest {
  kind: InferenceProviderKind;
  endpoint: string;
  apiKey?: string;
  modelCode: string;
  capability: InferenceProviderCapability;
  personalOAuthMode?: boolean;
  providerId?: string;
}

export interface TestInferenceProviderResponse {
  ok: boolean;
  latencyMs: number;
  error: string | null;
  responsePreview: string | null;
}

/** One provider the catalog upgrade merged others into. Platform only. */
export interface ConsolidationGroup {
  survivorId: string;
  survivorName: string;
  mergedProviderIds: string[];
  mergedModelCount: number;
}

/** `GET /api/v1/inference-providers/consolidation-report`. */
export interface ConsolidationReport {
  groups: ConsolidationGroup[];
}

/**
 * `GET /api/v1/inference-providers/status?capability=…` — which provider a run would use for a
 * capability right now, and whether it is healthy. Readable by every signed-in user, so it carries
 * no endpoint, model, key or raw error: only the admin-chosen display name and plain-language facts.
 */
export interface InferenceProviderStatus {
  capability: InferenceProviderCapability;
  /** Null when nothing of this capability is configured. */
  providerName: string | null;
  /** The last connection test's outcome; null when it has never been tested. */
  lastTestOk: boolean | null;
  /** ISO-8601 time of the last connection test, or null. */
  lastTestAt: string | null;
  healthy: boolean;
  /** True when the default failed its test and a healthier provider will be used instead. */
  usingFallback: boolean;
  /** A sentence to show the customer as-is, or null when there is nothing to say. */
  message: string | null;
}

/**
 * One row of `GET /api/v1/inference-providers/selectable` — a model capability a step editor may
 * offer to ANY member of the active workspace (Billing excluded). Mirrors the backend
 * `SelectableProviderResponse`: never an endpoint, key, health or error text.
 *
 * `id` is the model capability id, which is what a step config, an agent override or a voice
 * stores. `name` is the model's display name and `providerName` the account it belongs to, so a
 * picker labels a row `{name} · {providerName}`.
 */
export interface SelectableProvider {
  id: string;
  name: string;
  capability: InferenceProviderCapability;
  /** The vendor model code. */
  model: string;
  /** True for a platform-managed row ("ReelBolt Inference"), false for the workspace's own. */
  isPlatformManaged: boolean;
  kind: InferenceProviderKind;
  providerName: string;
  /**
   * Relative cost of a platform Chat model (the cheapest priced one is 1.0); null for a workspace's
   * own (BYO) model. A ratio only: no price ever reaches the browser.
   */
  costIndex: number | null;
}

/** The small tag a picker appends to a model that runs on the user's own computer. */
export const LOCAL_RUNNER_TAG = enT('libLabels.providers.localRunnerTag');

/** The label every picker shows for a selectable model capability. */
export function selectableLabel(p: Pick<SelectableProvider, 'name' | 'providerName'> & { kind?: InferenceProviderKind }, t: T = enT): string {
  const base = `${p.name} · ${p.providerName}`;
  return p.kind === 'LocalRunner' ? `${base} (${t('libLabels.providers.localRunnerTag')})` : base;
}

/**
 * One model capability flattened with its model and provider, for the screens that only ask "which
 * row serves capability X, and is it the default" (the admin dashboard, the setup checklist).
 * `id` is the model capability id; `name` is the model's display name.
 */
export interface ModelCapabilityRow {
  id: string;
  name: string;
  providerId: string;
  providerName: string;
  kind: InferenceProviderKind;
  modelCode: string;
  capability: InferenceProviderCapability;
  isDefault: boolean;
  /** True only when the capability, its model and its provider are all enabled. */
  isEnabled: boolean;
  lastTestAt: string | null;
  lastTestOk: boolean | null;
  lastTestError: string | null;
}

/** Every model capability in a provider tree, flattened. */
export function flattenProviders(providers: readonly InferenceProvider[]): ModelCapabilityRow[] {
  return providers.flatMap((provider) =>
    provider.models.flatMap((model) =>
      model.capabilities.map((cap) => ({
        id: cap.id,
        name: model.displayName,
        providerId: provider.id,
        providerName: provider.name,
        kind: provider.kind,
        modelCode: model.modelCode,
        capability: cap.capability,
        isDefault: cap.isDefault,
        isEnabled: cap.isEnabled && model.isEnabled && provider.isEnabled,
        lastTestAt: cap.lastTestAt,
        lastTestOk: cap.lastTestOk,
        lastTestError: cap.lastTestError,
      })),
    ),
  );
}
