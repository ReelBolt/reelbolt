import { describe, it, expect } from 'vitest';
import {
  CAPABILITIES_SERVED_BY_KIND,
  CAPABILITY_HINTS,
  CAPABILITY_OPTIONS,
  DEFAULT_NOUN,
  capabilityLabel,
  capabilityOptionsForKind,
  modelSuggestions,
  unsupportedCapabilities,
} from './provider-catalog';
import type { InferenceProviderKind } from '@/lib/types/inference-provider';

const ENHANCE = ['VideoUpscale', 'FrameInterpolation', 'VideoOutpaint'] as const;

describe('video-enhance capabilities in the provider catalogue (mirrors InferenceProvidersController.IsUnsupportedCombination)', () => {
  it('offers both video-enhance capabilities with plain labels', () => {
    expect(capabilityLabel('VideoUpscale')).toBe('Video Upscale (higher resolution)');
    expect(capabilityLabel('FrameInterpolation')).toBe('Frame Interpolation (smoother motion)');
    expect(CAPABILITY_OPTIONS.map((o) => o.value)).toEqual(expect.arrayContaining([...ENHANCE]));
    expect(DEFAULT_NOUN.VideoUpscale).toBe('video upscale');
    expect(DEFAULT_NOUN.FrameInterpolation).toBe('frame interpolation');
    expect(capabilityLabel('VideoOutpaint')).toBe('Video Extend (past the border)');
    expect(DEFAULT_NOUN.VideoOutpaint).toBe('video extend');
  });

  it('serves them from FalAi only', () => {
    const servers = (Object.keys(CAPABILITIES_SERVED_BY_KIND) as InferenceProviderKind[]).filter((kind) =>
      ENHANCE.some((c) => CAPABILITIES_SERVED_BY_KIND[kind].includes(c)),
    );
    expect(servers).toEqual(['FalAi']);
  });

  it('keeps FalAi\'s image, analysis and audio set', () => {
    expect(CAPABILITIES_SERVED_BY_KIND.FalAi).toEqual(
      expect.arrayContaining([
        'Segmentation',
        'VideoSegmentation',
        'VideoGeneration',
        'ImageGeneration',
        'MusicGeneration',
        'SoundEffectGeneration',
        ...ENHANCE,
      ]),
    );
  });

  it('refuses the video-enhance capabilities for every kind but FalAi', () => {
    expect(capabilityOptionsForKind('FalAi').map((o) => o.value)).toEqual(expect.arrayContaining([...ENHANCE]));
    for (const kind of [
      'AzureOpenAI',
      'OpenAICompatible',
      'Anthropic',
      'Gemini',
      'DeepSeek',
      'TypeSafe',
      'MiniMax',
      'FishAudio',
      'BlackForestLabs',
      'Higgsfield',
      'ComfyUI',
      'ElevenLabs',
      'LocalRunner',
    ] as const) {
      expect(unsupportedCapabilities(kind)).toEqual(expect.arrayContaining([...ENHANCE]));
    }
  });

  it('suggests the fal endpoint ids per capability, defaults first', () => {
    expect(modelSuggestions('FalAi', 'VideoUpscale')).toEqual([
      'fal-ai/topaz/upscale/video',
      'fal-ai/bytedance-upscaler/upscale/video',
    ]);
    expect(modelSuggestions('FalAi', 'FrameInterpolation')).toEqual(['fal-ai/rife/video', 'fal-ai/film/video']);
    expect(modelSuggestions('FalAi', 'VideoOutpaint')).toEqual([
      'fal-ai/wan-vace-14b/outpainting',
      'fal-ai/luma-dream-machine/ray-2/reframe',
    ]);
  });

  it('describes the video-enhance capabilities as fal-only', () => {
    expect(CAPABILITY_HINTS.VideoUpscale).toMatch(/fal\.ai only/);
    expect(CAPABILITY_HINTS.FrameInterpolation).toMatch(/fal\.ai only/);
    expect(CAPABILITY_HINTS.VideoOutpaint).toMatch(/fal\.ai only/);
  });
});
