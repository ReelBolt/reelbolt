import type {
  InferenceProviderCapability,
  InferenceProviderKind,
  SelectableCapability,
} from '@/lib/types/inference-provider';
import { DEPTH_MODELS, SEGMENTATION_MODELS } from '@/lib/types/image-document';
import { FALAI_IMAGE_MODELS, IMAGE_JOB_CAPABILITIES, isImageToolCapability } from '@/lib/inference/image-capabilities';

/**
 * What the admin forms know about each provider kind and each capability: labels, fixed vendor
 * endpoints, field wording, model suggestions, one-line hints and — most importantly — which
 * capabilities each kind may serve (`CAPABILITIES_SERVED_BY_KIND`, the web mirror of the backend's
 * `ProviderCapabilityUsage.ServableByKind`). Shared by the provider form, the add-model form and the
 * add-capability form so the three can never offer different things.
 *
 * `Compute` is deliberately absent from every option list here: it is served only by the system
 * provider, and no admin path creates one.
 */

export const KIND_OPTIONS: { value: InferenceProviderKind; label: string }[] = [
  { value: 'AzureOpenAI', label: 'Azure OpenAI' },
  { value: 'OpenAICompatible', label: 'OpenAI-compatible' },
  { value: 'Anthropic', label: 'Anthropic (Claude)' },
  { value: 'Gemini', label: 'Google Gemini' },
  { value: 'DeepSeek', label: 'DeepSeek' },
  { value: 'TypeSafe', label: 'TypeSafe AI (Jev)' },
  { value: 'MiniMax', label: 'MiniMax' },
  { value: 'FishAudio', label: 'Fish Audio' },
  { value: 'BlackForestLabs', label: 'Black Forest Labs (FLUX)' },
  { value: 'FalAi', label: 'fal.ai' },
  { value: 'Higgsfield', label: 'Higgsfield' },
  { value: 'ComfyUI', label: 'ComfyUI (self-hosted)' },
  // `ElevenLabs` is deliberately absent: the kind is dormant (product decision: music and sound effects through fal only).
  // `LocalRunner` is deliberately absent too: it is workspace-only, see `ORG_ONLY_KIND_OPTIONS`.
];

/** Display name of the LocalRunner kind. */
export const LOCAL_RUNNER_LABEL = 'Model on my computer (desktop app)';

/** Kinds only a workspace may create; the platform scope refuses them. */
export const ORG_ONLY_KIND_OPTIONS: { value: InferenceProviderKind; label: string }[] = [
  { value: 'LocalRunner', label: LOCAL_RUNNER_LABEL },
];

/** The kind options a scope's form offers: the platform list never carries a workspace-only kind. */
export function kindOptionsForScope(scope: 'platform' | 'org'): { value: InferenceProviderKind; label: string }[] {
  return scope === 'org' ? [...KIND_OPTIONS, ...ORG_ONLY_KIND_OPTIONS] : KIND_OPTIONS;
}

/**
 * The fal.ai video endpoint ids a VideoGeneration row may name, defaults first per direction. Mirrors
 * the backend catalogue (`FalAiVideoCatalog`), and — unlike the image ids — a wrong one is refused at
 * save time by `IsUnsupportedModel` rather than at run time, because every id here has a read schema
 * behind it. Prices are on each endpoint's model page; see docs/fal-ai-models.md section 10.
 */
export const FALAI_VIDEO_MODELS = [
  // image-to-video (the default first)
  'fal-ai/vidu/q4/image-to-video',
  'minimax/h3-max-turbo/image-to-video',
  'fal-ai/kling-video/v3/turbo/pro/image-to-video',
  'alibaba/wan-3.0/image-to-video',
  'blackforestlabs/flux-3/first-last-frame-to-video',
  'fal-ai/veo3.1/fast/image-to-video',
  'xai/grok-imagine-video/v1.5/lite/image-to-video',
  // text-to-video
  'fal-ai/kling-video/v3/turbo/pro/text-to-video',
  'minimax/h3-max-turbo/text-to-video',
  'alibaba/wan-3.0/text-to-video',
  'fal-ai/veo3.1',
] as const;

export const CAPABILITY_OPTIONS: { value: SelectableCapability; label: string }[] = [
  { value: 'Chat', label: 'Chat' },
  { value: 'Transcription', label: 'Transcription (ASR)' },
  { value: 'Vision', label: 'Vision (shot captioning)' },
  { value: 'Decision', label: 'Decision (Jev / logprob)' },
  { value: 'VideoGeneration', label: 'Video Generation' },
  { value: 'SpeechSynthesis', label: 'Speech Synthesis (TTS / voiceover)' },
  { value: 'Embedding', label: 'Embedding (semantic file search)' },
  { value: 'ImageGeneration', label: 'Image Generation (text-to-image)' },
  { value: 'ImageEdit', label: 'Image Edit (edit from a description)' },
  { value: 'ImageErase', label: 'Image Erase (remove objects)' },
  { value: 'ImageFill', label: 'Image Fill (paint in an area)' },
  { value: 'ImageExpand', label: 'Image Expand (outpaint)' },
  { value: 'ImageBackgroundReplace', label: 'Image Background Replace' },
  { value: 'ImageRelight', label: 'Image Relight' },
  { value: 'ImageUpscale', label: 'Image Upscale' },
  { value: 'Segmentation', label: 'Segmentation (subject cut-out)' },
  { value: 'Depth', label: 'Depth (depth map)' },
  { value: 'MusicGeneration', label: 'Music Generation (AudioGenerate steps)' },
  { value: 'SoundEffectGeneration', label: 'Sound Effect Generation (AudioGenerate steps)' },
  { value: 'VideoSegmentation', label: 'Video Segmentation (text-prompted, per frame)' },
  { value: 'ContentModeration', label: 'Content Moderation' },
  { value: 'Rerank', label: 'Rerank (documentation search)' },
  { value: 'WebSearch', label: 'Web search' },
  { value: 'AudioIsolation', label: 'Audio Isolation (voice cleanup)' },
  { value: 'VideoUpscale', label: 'Video Upscale (higher resolution)' },
  { value: 'FrameInterpolation', label: 'Frame Interpolation (smoother motion)' },
  { value: 'VideoOutpaint', label: 'Video Extend (past the border)' },
  { value: 'LipSync', label: 'Lip-sync' },
];

/** The noun each capability's default switch names ("Set as default upscale provider"). */
export const DEFAULT_NOUN: Record<InferenceProviderCapability, string> = {
  Chat: 'chat',
  Transcription: 'transcription',
  Vision: 'vision',
  Decision: 'decision',
  VideoGeneration: 'video generation',
  VideoSegmentation: 'video segmentation',
  SpeechSynthesis: 'speech synthesis',
  Embedding: 'embedding',
  ImageGeneration: 'image generation',
  ImageEdit: 'image edit',
  ImageErase: 'object removal',
  ImageFill: 'area fill',
  ImageExpand: 'expand',
  ImageBackgroundReplace: 'background replace',
  ImageRelight: 'relight',
  ImageUpscale: 'upscale',
  Segmentation: 'segmentation',
  Depth: 'depth',
  Compute: 'compute',
  MusicGeneration: 'music generation',
  SoundEffectGeneration: 'sound effect',
  ContentModeration: 'content moderation',
  Rerank: 'rerank',
  WebSearch: 'web search',
  AudioIsolation: 'audio cleanup',
  VideoUpscale: 'video upscale',
  FrameInterpolation: 'frame interpolation',
  VideoOutpaint: 'video extend',
  LipSync: 'lip-sync',
};

/** Anthropic's production API. Prefilled because the backend requires a non-empty endpoint. */
export const ANTHROPIC_DEFAULT_ENDPOINT = 'https://api.anthropic.com';

/** Google Gemini's OpenAI compatibility endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const GEMINI_DEFAULT_ENDPOINT = 'https://generativelanguage.googleapis.com/v1beta/openai/';

/** DeepSeek's OpenAI compatibility endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const DEEPSEEK_DEFAULT_ENDPOINT = 'https://api.deepseek.com';

/** MiniMax's API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const MINIMAX_DEFAULT_ENDPOINT = 'https://api.minimax.io';

/** TypeSafe's decision-gate API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const TYPESAFE_DEFAULT_ENDPOINT = 'https://api.typesafe.ai';

/** Fish Audio's hosted API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const FISHAUDIO_DEFAULT_ENDPOINT = 'https://api.fish.audio';

/** Black Forest Labs' (FLUX) API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const BLACKFORESTLABS_DEFAULT_ENDPOINT = 'https://api.bfl.ai';

/**
 * fal.ai's queue API. Prefilled because the backend requires a non-empty endpoint. The MODEL is the
 * endpoint id (`fal-ai/evf-sam`) rather than a name in a body, which is why the two fields read so
 * differently here: this URL is the queue, that value is the model.
 */
export const FALAI_DEFAULT_ENDPOINT = 'https://queue.fal.run';
/** ElevenLabs' API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const ELEVENLABS_DEFAULT_ENDPOINT = 'https://api.elevenlabs.io';

/** Higgsfield's API endpoint. Prefilled because the backend requires a non-empty endpoint. */
export const HIGGSFIELD_DEFAULT_ENDPOINT = 'https://api.higgsfield.ai';

/** The optional `comfyui` compose service (gpu profile) as the in-network consumers reach it. Prefilled because the backend requires a non-empty endpoint. */
export const COMFYUI_DEFAULT_ENDPOINT = 'http://comfyui:8188';

/**
 * Per-kind field wording and examples. Keyed by kind rather than branched on `isAzure`, so a third
 * kind does not turn every label into a nested ternary.
 */
export const KIND_FIELD_HINTS: Record<
  InferenceProviderKind,
  { endpointLabel: string; modelLabel: string; endpointPlaceholder: string; modelPlaceholder: string }
> = {
  AzureOpenAI: {
    endpointLabel: 'Endpoint',
    modelLabel: 'Deployment name',
    endpointPlaceholder: 'https://my-resource.openai.azure.com',
    modelPlaceholder: 'gpt-4o-mini',
  },
  OpenAICompatible: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: 'http://localhost:8000/v1',
    modelPlaceholder: 'meta-llama/Llama-3-8b',
  },
  Anthropic: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: ANTHROPIC_DEFAULT_ENDPOINT,
    modelPlaceholder: 'claude-opus-5',
  },
  Gemini: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: GEMINI_DEFAULT_ENDPOINT,
    modelPlaceholder: 'gemini-2.5-flash',
  },
  DeepSeek: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: DEEPSEEK_DEFAULT_ENDPOINT,
    modelPlaceholder: 'deepseek-chat',
  },
  MiniMax: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: MINIMAX_DEFAULT_ENDPOINT,
    modelPlaceholder: 'MiniMax-H3',
  },
  TypeSafe: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: TYPESAFE_DEFAULT_ENDPOINT,
    modelPlaceholder: 'jev-latest',
  },
  FishAudio: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: FISHAUDIO_DEFAULT_ENDPOINT,
    modelPlaceholder: 's2.1-pro-free',
  },
  BlackForestLabs: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: BLACKFORESTLABS_DEFAULT_ENDPOINT,
    modelPlaceholder: 'flux-2-pro',
  },
  FalAi: {
    endpointLabel: 'Queue base URL',
    modelLabel: 'Endpoint id',
    endpointPlaceholder: FALAI_DEFAULT_ENDPOINT,
    modelPlaceholder: 'fal-ai/sam-3-1/image',
  },
  Higgsfield: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: HIGGSFIELD_DEFAULT_ENDPOINT,
    modelPlaceholder: 'dop-turbo',
  },
  LocalRunner: {
    endpointLabel: 'Computer',
    modelLabel: 'Model name',
    endpointPlaceholder: '',
    modelPlaceholder: 'llama3.2',
  },
  ComfyUI: {
    endpointLabel: 'Server URL',
    modelLabel: 'Workflow template',
    endpointPlaceholder: COMFYUI_DEFAULT_ENDPOINT,
    modelPlaceholder: 'wan2.2-ti2v-5b-t2v',
  },
  ElevenLabs: {
    endpointLabel: 'Base URL',
    modelLabel: 'Model',
    endpointPlaceholder: ELEVENLABS_DEFAULT_ENDPOINT,
    modelPlaceholder: 'music_v2_5',
  },
};

/**
 * Kinds whose capability is implied by the kind: switching to one selects it instead of leaving an
 * unsupported value behind. TypeSafe genuinely serves exactly one capability; Fish Audio serves two
 * (SpeechSynthesis and Transcription) and text-to-speech is simply the one a Fish Audio row usually
 * means, so it is the value selected on switch — not the only one on offer.
 *
 * MiniMax deliberately has NO entry any more: it serves three capabilities (VideoGeneration,
 * ImageGeneration, ImageEdit), so no single one of them is "the" capability a switch could mean.
 * The pin was honest while VideoGeneration was all it served; keeping it now would silently rewrite
 * an image row into a video row every time the kind field was touched. It falls through to
 * `fallbackCapability` instead, the same way any other multi-capability kind does.
 */
export const SOLE_CAPABILITY: Partial<Record<InferenceProviderKind, SelectableCapability>> = {
  TypeSafe: 'Decision',
  Higgsfield: 'VideoGeneration',
  ComfyUI: 'VideoGeneration',
  FishAudio: 'SpeechSynthesis',
  // fal.ai now serves eleven capabilities (Segmentation, Depth, VideoGeneration, the two
  // whole-picture image ones and the six per-tool jobs), but subject detection is what a fal.ai row
  // has always meant and the editor's detect control reads it, so it is the value selected on switch
  // (as for Fish Audio).
  FalAi: 'Segmentation',
};

/**
 * The capability to select when a kind switch invalidates the one currently chosen: the kind's sole
 * capability when it has exactly one, otherwise the first capability that kind actually serves.
 *
 * The "otherwise" half is load-bearing, not a convenience. A fixed `?? 'Chat'` fallback leaves
 * MiniMax — which serves VideoGeneration, ImageGeneration and ImageEdit and no chat at all —
 * rendering a value absent from its own option list: a blank Select submitting a combination the
 * backend then rejects with a 400.
 */
export function fallbackCapability(kind: InferenceProviderKind): SelectableCapability {
  const sole = SOLE_CAPABILITY[kind];
  if (sole) return sole;
  const unsupported = unsupportedCapabilities(kind);
  return CAPABILITY_OPTIONS.find((o) => !unsupported.includes(o.value))?.value ?? 'Chat';
}

/** Model/deployment suggestions per kind (and capability where it matters). Free text stays allowed: these are hints, not an allowlist. */
export function modelSuggestions(kind: InferenceProviderKind, capability: InferenceProviderCapability): string[] {
  switch (kind) {
    case 'FishAudio':
      // Fish Audio is one of the few kinds whose model namespace depends on the capability: a
      // transcription row transcribes with the ASR models, a voice row speaks with the TTS ones.
      // Both lists come from the vendor's own OpenAPI document (api.fish.audio/openapi.json).
      return capability === 'Transcription'
        ? ['transcribe-1', 'transcribe-1-pro']
        : ['s2.1-pro', 's2.1-pro-free', 's2-pro', 's1', 'drama-3-preview'];
    case 'FalAi':
      // fal names a model as the endpoint path itself ('fal-ai/evf-sam'), so these ids are the real
      // ones from the vendor's catalogue — a wrong one is a 404 at run time and nothing else catches
      // it. The segmentation list is shared with the editor's own detect control rather than copied.
      // Each picture capability gets the endpoints of ITS job, default first: the API refuses a row
      // whose endpoint serves another job, and the editor sends the row's endpoint to its job.
      if (isImageToolCapability(capability)) return [...FALAI_IMAGE_MODELS[capability]];
      if (capability === 'VideoGeneration') return [...FALAI_VIDEO_MODELS];
      if (capability === 'AudioIsolation') return ['fal-ai/elevenlabs/audio-isolation'];
      if (capability === 'VideoUpscale') return ['fal-ai/topaz/upscale/video', 'fal-ai/bytedance-upscaler/upscale/video'];
      if (capability === 'FrameInterpolation') return ['fal-ai/rife/video', 'fal-ai/film/video'];
      if (capability === 'VideoOutpaint') return ['fal-ai/wan-vace-14b/outpainting', 'fal-ai/luma-dream-machine/ray-2/reframe'];
      if (capability === 'LipSync') return ['minimax/h3-max/lip-sync/image-to-video', 'fal-ai/kling-video/ai-avatar/v2/standard'];
      if (capability === 'MusicGeneration') return ['fal-ai/elevenlabs/music'];
      if (capability === 'SoundEffectGeneration') return ['fal-ai/elevenlabs/sound-effects/v2', 'fal-ai/mmaudio-v2/text-to-audio'];
      return capability === 'Depth' ? [...DEPTH_MODELS] : [...SEGMENTATION_MODELS];
    case 'TypeSafe':
      return ['jev-latest'];
    case 'MiniMax':
      // One kind, three capabilities: the video models answer VideoGeneration, and the single image
      // model answers BOTH image capabilities (POST /v1/image_generation).
      return capability === 'ImageGeneration' || capability === 'ImageEdit'
        ? ['image-01']
        : ['MiniMax-H3', 'MiniMax-H3-Max'];
    case 'BlackForestLabs':
      // FLUX names are the path segments of api.bfl.ai rather than model ids in a request body,
      // which is why they read as endpoints — and why the whole URL, not just this value, identifies
      // the model. The two mask jobs need the fill model.
      return capability === 'ImageErase' || capability === 'ImageFill'
        ? ['flux-pro-1.0-fill']
        : ['flux-2-pro', 'flux-2-klein-4b', 'flux-kontext-pro', 'flux-pro-1.1'];
    case 'LocalRunner':
      // Suggestions come from the chosen computer's reported models, not a static list.
      return [];
    case 'ElevenLabs':
      // Dormant (product decision): nothing to suggest for a kind the backend refuses.
      return [];
    case 'ComfyUI':
      // The ONLY two values the backend accepts: vetted first-party workflow templates, by exact name.
      return ['wan2.2-ti2v-5b-t2v', 'wan2.2-ti2v-5b-i2v'];
    case 'Anthropic':
      return ['claude-opus-5', 'claude-sonnet-5', 'claude-haiku-4-5-20251001'];
    case 'Gemini':
      return ['gemini-2.5-flash', 'gemini-2.5-pro'];
    case 'DeepSeek':
      return ['deepseek-chat', 'deepseek-reasoner'];
    case 'OpenAICompatible':
      if (capability === 'Embedding') return ['Qwen/Qwen3-Embedding-0.6B', 'text-embedding-3-small'];
      if (capability === 'SpeechSynthesis') return ['speaches-ai/Kokoro-82M-v1.0-ONNX', 'tts-1', 'gpt-4o-mini-tts'];
      return capability === 'Transcription'
        ? ['Systran/faster-whisper-small', 'Systran/faster-whisper-large-v3', 'whisper-1']
        : capability === 'Decision'
          ? []
          : [];
    default:
      if (capability === 'Embedding') return ['text-embedding-3-large', 'text-embedding-3-small'];
      return capability === 'Transcription' ? ['whisper-1', 'gpt-4o-transcribe'] : ['gpt-4o-mini', 'gpt-4o'];
  }
}

/** One line of guidance per capability, shown under the selector. */
export const CAPABILITY_HINTS: Record<InferenceProviderCapability, string> = {
  Chat: 'Serves agent completions.',
  Transcription:
    'Speech-to-text for VideoAnalyze steps and voiceover word alignment. Azure OpenAI and OpenAI-compatible rows call /audio/transcriptions; a Fish Audio row calls Fish Audio\u2019s hosted /v1/asr and returns phrases with start/end times but no per-word timings.',
  Vision: "Optional shot captioning in VideoAnalyze; must accept image input.",
  Decision: 'Decision-gate models (TypeSafe/Jev, OpenAI logprobs). Never used for chat.',
  VideoSegmentation:
    'Text-prompted object tracking for VideoAnalyze (method=segmentation): fal SAM 3.1 video-rle follows a plain-words prompt per frame and the compile step applies an effect through the returned mask. Billed per 16 frames of input; the source leaves the host only with AllowSourceMediaEgress; trial plans are not entitled.',
  VideoGeneration:
    'Video for VideoGenerate steps: MiniMax (MiniMax-H3, MiniMax-H3-Max), Higgsfield (an Endpoint ID such as kling-video/v3.0/std/text-to-video), the self-hosted ComfyUI (a vetted Wan 2.2 template only), or fal.ai (an endpoint id from docs/fal-ai-models.md section 10, image-to-video or text-to-video, model set per step). Spends real money; budget-capped.',
  SpeechSynthesis: 'Text-to-speech for Voiceover steps: Fish Audio (cloud or a self-hosted fish-speech server), or any OpenAI-compatible /v1/audio/speech server such as the bundled whisper (speaches) container with a kokoro voice.',
  Embedding:
    'Text embeddings for semantic file search (Azure OpenAI or an OpenAI-compatible /v1/embeddings server, e.g. the bundled `embeddings` service: http://embeddings:80, model Qwen/Qwen3-Embedding-0.6B). Each model/dimension gets its own vector index; after switching, reindex project files.',
  ImageGeneration:
    'Text-to-image for thumbnails and cover art, optionally guided by a reference image. Spends real money on every image; unlike a chat row there is nothing to fall back to when the credits run out.',
  ImageEdit:
    'The picture editor\u2019s \u201cEdit from a description\u201d tool: change a picture with a sentence (\u201cmake the sky purple\u201d). Until every tool below has a row of its own, a tool with none falls back to this row when its kind can do that job.',
  ImageErase:
    'The \u201cRemove objects\u201d tool: removes what the user paints over and fills the hole from its surroundings. fal.ai (fal-ai/object-removal/mask), OpenAI-compatible or Black Forest Labs (flux-pro-1.0-fill).',
  ImageFill:
    'The \u201cFill an area\u201d tool: regenerates a painted area from a prompt. fal.ai (fal-ai/flux-pro/v1/fill), OpenAI-compatible or Black Forest Labs (flux-pro-1.0-fill).',
  ImageExpand: 'The \u201cExpand\u201d tool: outpaints a picture to larger bounds. fal.ai only (fal-ai/bria/expand by default, which takes a prompt and grows to a shape; fal-ai/flux-2-pro/outpaint takes margins only).',
  ImageBackgroundReplace:
    'The \u201cReplace background\u201d tool: keeps the subject and generates a new background. fal.ai only (fal-ai/bria/background/replace).',
  ImageRelight:
    'The \u201cRelight\u201d tool: changes a picture\u2019s lighting. fal.ai only (fal-ai/image-apps-v2/relighting, fal-ai/iclight-v2, bria/fibo-edit/relight).',
  ImageUpscale:
    'The \u201cUpscale\u201d tool: raises a picture\u2019s resolution. fal.ai only (fal-ai/seedvr/upscale/image, or Topaz for crisp text).',
  Segmentation:
    'Cuts a subject out of a picture with alpha, from a prompt like "the person". This is what makes a layer in the image editor a cut-out instead of a whole image. fal.ai only (fal-ai/sam-3-1/image, fal-ai/evf-sam, fal-ai/sam2/image, fal-ai/birefnet/v2). Paid on every call.',
  Depth:
    'Reads how far away each part of a picture is, as a depth map. fal.ai only (fal-ai/imageutils/depth, fal-ai/imageutils/marigold-depth). Paid on every call.',
  Compute: 'The platform\u2019s own render, encode and media analysis. Served only by the system provider; never added by hand.',
  MusicGeneration:
    'Music tracks from a prompt or a composition plan: fal.ai only (fal-ai/elevenlabs/music). Spends real money per minute of music.',
  SoundEffectGeneration:
    'Sound effects from a description: fal.ai only (fal-ai/elevenlabs/sound-effects/v2, fal-ai/mmaudio-v2/text-to-audio). Spends real money per second.',
  ContentModeration:
    'Content-moderation decisions for uploads, generated media and publishes. Served by an OpenAI-compatible row only (the vendor moderation endpoint, e.g. omni-moderation-latest); video and audio are sampled into derived frames plus text first, and an unresolved provider means moderation is off rather than a fallback to a chat model.',
  Rerank:
    'Reorders the assistant\u2019s documentation search results by relevance. Served by an OpenAI-compatible row only (POST {endpoint}/rerank, e.g. a text-embeddings-inference reranker). A self-hosted reranker sends nothing off the host; a hosted one receives the question and the candidate passages and is billed per search. An unpriced or missing provider just means the search keeps its built-in order.',
  WebSearch:
    'Lets workflow agents look things up on the web. Each search is billed per query. Gemini: Google requires search suggestions to be shown with grounded results; results are not cached or shared with other users\u2019 runs - ask before enabling for shared workspaces. OpenAI-compatible: only api.openai.com.',
  AudioIsolation:
    'Removes background noise and music from a recording, leaving the voice: fal.ai only (fal-ai/elevenlabs/audio-isolation). Spends real money per minute of audio.',
  VideoUpscale:
    'Raises a clip\u2019s resolution: fal.ai only (fal-ai/topaz/upscale/video by default, fal-ai/bytedance-upscaler/upscale/video). Spends real money per output second of video, priced by resolution tier.',
  FrameInterpolation:
    'Raises a clip\u2019s frame rate for smoother motion: fal.ai only (fal-ai/rife/video by default, fal-ai/film/video). Spends real money per compute second.',
  VideoOutpaint:
    'Extends a clip past its border, generating new picture around it: fal.ai only (fal-ai/wan-vace-14b/outpainting by default, fal-ai/luma-dream-machine/ray-2/reframe). Spends real money per output second of video.',
  LipSync:
    'Animates a still portrait to speak a recording (talking avatars): fal.ai only, and the model must be a talking-head one (minimax/h3-max/lip-sync/image-to-video by default, fal-ai/kling-video/ai-avatar/v2/standard). Add these here, not under Video generation. Spends real money per output second of video.',
};

/**
 * The capabilities each kind may serve — the web mirror of the backend's
 * `ProviderCapabilityUsage.ServableByKind` (itself held to `InferenceProvidersController.IsUnsupportedCombination`
 * by a test). A POSITIVE allowlist, so a capability added later is offered to no kind until it is listed
 * here, which is the same discipline the backend applies (its fallthrough is ALLOW, so its image rules
 * are allowlists too).
 *
 * Anthropic, Gemini and DeepSeek have no speech-to-text API. TypeSafe and OpenJev-style backends serve
 * Decision only. MiniMax serves video and the two whole-picture image capabilities. Fish Audio serves
 * speech synthesis and transcription. OpenAI-compatible and Black Forest Labs also serve the two MASK
 * jobs (erase and fill), through the masked-edit route each documents. fal.ai serves segmentation, depth
 * and every picture capability, and is the only kind wired for expand, background replace, relight and
 * upscale.
 */
export const CAPABILITIES_SERVED_BY_KIND: Record<InferenceProviderKind, readonly SelectableCapability[]> = {
  AzureOpenAI: ['Chat', 'Transcription', 'Vision', 'Decision', 'Embedding'],
  OpenAICompatible: [
    'Chat', 'Transcription', 'Vision', 'Decision', 'Embedding', 'SpeechSynthesis',
    'ImageGeneration', 'ImageEdit', 'ImageErase', 'ImageFill', 'ContentModeration', 'Rerank', 'WebSearch',
  ],
  Anthropic: ['Chat', 'Vision'],
  Gemini: ['Chat', 'Vision', 'WebSearch'],
  DeepSeek: ['Chat', 'Vision', 'Decision', 'WebSearch'],
  TypeSafe: ['Decision'],
  MiniMax: ['VideoGeneration', 'ImageGeneration', 'ImageEdit'],
  FishAudio: ['SpeechSynthesis', 'Transcription'],
  BlackForestLabs: ['ImageGeneration', 'ImageEdit', 'ImageErase', 'ImageFill'],
  // VideoGeneration was appended when the image-to-video wave landed: fal hosts video models behind
  // the same queue contract its images use, so it is one more capability of the same kind rather than
  // a second kind.
  FalAi: [
    'Segmentation', 'Depth', 'VideoSegmentation', 'VideoGeneration', 'ImageGeneration', 'ImageEdit',
    ...IMAGE_JOB_CAPABILITIES,
    'MusicGeneration', 'SoundEffectGeneration', 'AudioIsolation',
    'VideoUpscale', 'FrameInterpolation', 'VideoOutpaint', 'LipSync',
  ],
  Higgsfield: ['VideoGeneration'],
  ComfyUI: ['VideoGeneration'],
  LocalRunner: ['Chat', 'Vision', 'Embedding'],
  // Music and sound effects mirror the backend allowlist (InferenceProvidersController.IsUnsupportedCombination):
  // only fal.ai serves them. The ElevenLabs kind is dormant (product decision) and the backend refuses it every
  // capability, so it serves nothing here either.
  ElevenLabs: [],
};

/** Capabilities the backend refuses for a kind: everything not on its allowlist. */
export function unsupportedCapabilities(kind: InferenceProviderKind): SelectableCapability[] {
  const served = CAPABILITIES_SERVED_BY_KIND[kind];
  if (!served) return [];
  return CAPABILITY_OPTIONS.map((o) => o.value).filter((c) => !served.includes(c));
}

/** The fixed, well-known endpoint of each kind that has one; prefilled on a kind switch. */
export const KIND_DEFAULT_ENDPOINT: Partial<Record<InferenceProviderKind, string>> = {
  Anthropic: ANTHROPIC_DEFAULT_ENDPOINT,
  Gemini: GEMINI_DEFAULT_ENDPOINT,
  DeepSeek: DEEPSEEK_DEFAULT_ENDPOINT,
  MiniMax: MINIMAX_DEFAULT_ENDPOINT,
  TypeSafe: TYPESAFE_DEFAULT_ENDPOINT,
  BlackForestLabs: BLACKFORESTLABS_DEFAULT_ENDPOINT,
  FalAi: FALAI_DEFAULT_ENDPOINT,
  Higgsfield: HIGGSFIELD_DEFAULT_ENDPOINT,
  ComfyUI: COMFYUI_DEFAULT_ENDPOINT,
};

/** The capabilities a kind may serve, as select options, in the house order. */
export function capabilityOptionsForKind(kind: InferenceProviderKind): { value: SelectableCapability; label: string }[] {
  const refused = unsupportedCapabilities(kind);
  return CAPABILITY_OPTIONS.filter((o) => !refused.includes(o.value));
}

/** A capability's long label ("Chat", "Transcription (ASR)"), or its raw name for `Compute`. */
export function capabilityLabel(capability: InferenceProviderCapability): string {
  return CAPABILITY_OPTIONS.find((o) => o.value === capability)?.label ?? capability;
}

/**
 * The model-code placeholder for a kind and capability. Fish Audio, MiniMax and fal.ai name
 * different models per capability, so the placeholder follows the capability rather than
 * advertising, say, a TTS model on an ASR row.
 */
export function modelPlaceholderFor(kind: InferenceProviderKind, capability: InferenceProviderCapability): string {
  const kindModelPlaceholder = (KIND_FIELD_HINTS[kind] ?? KIND_FIELD_HINTS.OpenAICompatible).modelPlaceholder;
  if (kind === 'FishAudio' && capability === 'Transcription') return 'transcribe-1';
  if (kind === 'MiniMax' && (capability === 'ImageGeneration' || capability === 'ImageEdit')) return 'image-01';
  if (kind === 'FalAi' && capability === 'VideoGeneration') return FALAI_VIDEO_MODELS[0];
  if (kind === 'FalAi' && capability === 'Depth') return DEPTH_MODELS[0];
  if (kind === 'FalAi' && isImageToolCapability(capability)) return FALAI_IMAGE_MODELS[capability][0];
  return kindModelPlaceholder;
}
