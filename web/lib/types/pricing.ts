import type { InferenceProviderCapability } from './inference-provider';

/**
 * The billable physical unit a price is quoted in. Mirrors the backend `UsageKind` enum exactly;
 * append-only there, so appending here is the only safe edit.
 */
export type UsageKind =
  | 'LlmInputTokens'
  | 'LlmOutputTokens'
  | 'LlmCachedInputTokens'
  | 'EmbeddingTokens'
  | 'AsrAudioSeconds'
  | 'TtsInputBytes'
  | 'VideoGenSeconds'
  | 'RenderOutputSeconds'
  | 'EncodeOutputSeconds'
  | 'AnalyzeMediaSeconds'
  | 'StorageGbDay'
  | 'ImageGenImages'
  | 'ImageEditImages'
  | 'SegmentationImages'
  | 'DepthImages'
  | 'MusicGenMinutes'
  | 'SoundEffectGenSeconds'
  | 'VideoSegmentationFrames'
  | 'ModerationCalls'
  | 'RerankSearches'
  | 'WebSearches'
  | 'AudioCleanupMinutes'
  | 'VideoEnhanceSeconds'
  | 'VideoEnhanceComputeSeconds';

/** Every unit, in enum order (for selects that list them all). */
export const USAGE_KINDS: readonly UsageKind[] = [
  'LlmInputTokens',
  'LlmOutputTokens',
  'LlmCachedInputTokens',
  'EmbeddingTokens',
  'AsrAudioSeconds',
  'TtsInputBytes',
  'VideoGenSeconds',
  'RenderOutputSeconds',
  'EncodeOutputSeconds',
  'AnalyzeMediaSeconds',
  'StorageGbDay',
  'ImageGenImages',
  'ImageEditImages',
  'SegmentationImages',
  'DepthImages',
  'MusicGenMinutes',
  'SoundEffectGenSeconds',
  'VideoSegmentationFrames',
  'ModerationCalls',
  'RerankSearches',
  'WebSearches',
  'AudioCleanupMinutes',
  'VideoEnhanceSeconds',
  'VideoEnhanceComputeSeconds',
];

const LLM_UNITS: readonly UsageKind[] = ['LlmInputTokens', 'LlmOutputTokens', 'LlmCachedInputTokens'];

/**
 * The units a capability can be priced in: the web mirror of the backend's
 * `ProviderCapabilityUsage.ByCapability`. The add-price form offers exactly these.
 */
export const UNITS_BY_CAPABILITY: Record<InferenceProviderCapability, readonly UsageKind[]> = {
  Chat: LLM_UNITS,
  Vision: LLM_UNITS,
  Decision: LLM_UNITS,
  Embedding: ['EmbeddingTokens'],
  Transcription: ['AsrAudioSeconds'],
  SpeechSynthesis: ['TtsInputBytes'],
  VideoGeneration: ['VideoGenSeconds'],
  ImageGeneration: ['ImageGenImages'],
  ImageEdit: ['ImageEditImages'],
  ImageErase: ['ImageEditImages'],
  ImageFill: ['ImageEditImages'],
  ImageExpand: ['ImageEditImages'],
  ImageBackgroundReplace: ['ImageEditImages'],
  ImageRelight: ['ImageEditImages'],
  ImageUpscale: ['ImageEditImages'],
  Segmentation: ['SegmentationImages'],
  Depth: ['DepthImages'],
  Compute: ['RenderOutputSeconds', 'EncodeOutputSeconds', 'AnalyzeMediaSeconds'],
  MusicGeneration: ['MusicGenMinutes'],
  SoundEffectGeneration: ['SoundEffectGenSeconds'],
  VideoSegmentation: ['VideoSegmentationFrames'],
  ContentModeration: ['ModerationCalls'],
  Rerank: ['RerankSearches'],
  WebSearch: ['WebSearches'],
  AudioIsolation: ['AudioCleanupMinutes'],
  VideoUpscale: ['VideoEnhanceSeconds'],
  FrameInterpolation: ['VideoEnhanceComputeSeconds'],
  VideoOutpaint: ['VideoEnhanceSeconds'],
  LipSync: ['VideoGenSeconds'],
};

/** Who pays the provider. Mirrors `UsageBillingMode`. */
export type UsageBillingMode = 'Platform' | 'Byo' | 'Runner';

/**
 * One billing-mode multiplier (`GET /api/v1/pricing/multipliers`). A null `usageKind` is the
 * default for the mode; a kind-specific row wins over it.
 */
export interface RateMultiplier {
  id: string;
  billingMode: UsageBillingMode;
  usageKind: UsageKind | null;
  multiplier: number;
  effectiveFrom: string;
  effectiveTo: string | null;
}

/** Body of `POST /api/v1/pricing/multipliers`. */
export interface CreateRateMultiplierRequest {
  billingMode: UsageBillingMode;
  usageKind: UsageKind | null;
  multiplier: number;
  effectiveFrom: string | null;
}

/**
 * `GET /api/v1/pricing/settings`. Credits = cost × `markup` ÷ `creditUsdValue`, unless a price sets
 * its own credits. Only `markup` is editable; the credit value changes only by migration.
 */
export interface PricingSettings {
  markup: number;
  creditUsdValue: number;
  updatedAt: string;
}

/** Body of `PUT /api/v1/pricing/settings`. */
export interface UpdatePricingSettingsRequest {
  markup: number;
}

/**
 * One row of `GET /api/v1/pricing/coverage`: an enabled platform capability with a unit it can be
 * metered in and no open price for it.
 */
export interface PriceCoverageGap {
  capabilityId: string;
  providerId: string;
  providerName: string;
  modelDisplayName: string;
  capability: InferenceProviderCapability;
  missingUnits: UsageKind[];
}

/** One suggestion of `GET /api/v1/pricing/price-book?kind=&capability=&model=` (add-price pre-fill). */
export interface PriceBookEntry {
  unit: UsageKind;
  variant: string | null;
  costUsdPerUnit: number;
  notes: string | null;
}

/** Body of `POST …/capabilities/{capId}/prices`. Costs and overrides are per ONE unit. */
export interface CreatePriceRequest {
  unit: UsageKind;
  variant?: string;
  costUsdPerUnit: number;
  creditsMillisPerUnitOverride?: number;
  effectiveFrom?: string;
  notes?: string;
}
