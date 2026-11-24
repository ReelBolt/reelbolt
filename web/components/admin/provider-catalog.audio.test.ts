import { describe, it, expect } from 'vitest';
import {
  CAPABILITIES_SERVED_BY_KIND,
  CAPABILITY_HINTS,
  CAPABILITY_OPTIONS,
  KIND_DEFAULT_ENDPOINT,
  KIND_OPTIONS,
  capabilityLabel,
  capabilityOptionsForKind,
  modelSuggestions,
  unsupportedCapabilities,
} from './provider-catalog';
import type { InferenceProviderKind } from '@/lib/types/inference-provider';

const AUDIO = ['MusicGeneration', 'SoundEffectGeneration', 'AudioIsolation'] as const;

describe('audio capabilities in the provider catalogue (mirrors InferenceProvidersController.IsUnsupportedCombination)', () => {
  it('offers both audio capabilities with plain labels, and never offers the dormant ElevenLabs kind (decision 7)', () => {
    expect(KIND_OPTIONS.map((o) => o.value)).not.toContain('ElevenLabs');
    expect(KIND_OPTIONS.map((o) => o.value)).toContain('FalAi');
    expect(capabilityLabel('MusicGeneration')).toBe('Music Generation (AudioGenerate steps)');
    expect(capabilityLabel('SoundEffectGeneration')).toBe('Sound Effect Generation (AudioGenerate steps)');
    expect(capabilityLabel('AudioIsolation')).toBe('Audio Isolation (voice cleanup)');
    expect(CAPABILITY_OPTIONS.map((o) => o.value)).toEqual(expect.arrayContaining([...AUDIO]));
  });

  it('serves them from FalAi only', () => {
    const servers = (Object.keys(CAPABILITIES_SERVED_BY_KIND) as InferenceProviderKind[]).filter((kind) =>
      AUDIO.some((c) => CAPABILITIES_SERVED_BY_KIND[kind].includes(c)),
    );
    expect(servers).toEqual(['FalAi']);
  });

  it('keeps FalAi\'s image and analysis set, and gives the dormant ElevenLabs kind nothing at all', () => {
    // VideoSegmentation is named on purpose: the backend allowlist accepts it for fal.ai and this map is
    // its web mirror. It drifted once - the capability reached the options and the controller but not this
    // row - and the provider form silently stopped offering it.
    expect(CAPABILITIES_SERVED_BY_KIND.FalAi).toEqual(
      expect.arrayContaining(['Segmentation', 'VideoSegmentation', 'VideoGeneration', 'ImageGeneration', ...AUDIO]),
    );
    expect([...CAPABILITIES_SERVED_BY_KIND.ElevenLabs]).toEqual([]);
    expect(capabilityOptionsForKind('ElevenLabs')).toEqual([]);
    expect(unsupportedCapabilities('ElevenLabs')).toEqual(expect.arrayContaining([...AUDIO, 'Chat']));
  });

  it('refuses the audio capabilities for every kind but FalAi', () => {
    expect(capabilityOptionsForKind('FalAi').map((o) => o.value)).toEqual(expect.arrayContaining([...AUDIO]));
    for (const kind of ['AzureOpenAI', 'OpenAICompatible', 'Anthropic', 'Gemini', 'DeepSeek', 'TypeSafe', 'MiniMax', 'FishAudio', 'BlackForestLabs', 'Higgsfield', 'ComfyUI', 'ElevenLabs'] as const) {
      expect(unsupportedCapabilities(kind)).toEqual(expect.arrayContaining([...AUDIO]));
    }
  });

  it('prefills nothing and suggests nothing for ElevenLabs', () => {
    expect(KIND_DEFAULT_ENDPOINT.ElevenLabs).toBeUndefined();
    expect(modelSuggestions('ElevenLabs', 'MusicGeneration')).toEqual([]);
  });

  it('suggests the fal endpoint ids per capability, defaults first', () => {
    expect(modelSuggestions('FalAi', 'MusicGeneration')).toEqual(['fal-ai/elevenlabs/music']);
    expect(modelSuggestions('FalAi', 'SoundEffectGeneration')).toEqual(['fal-ai/elevenlabs/sound-effects/v2', 'fal-ai/mmaudio-v2/text-to-audio']);
  });

  it('suggests the audio isolation endpoint for FalAi', () => {
    expect(modelSuggestions('FalAi', 'AudioIsolation')).toEqual(['fal-ai/elevenlabs/audio-isolation']);
  });

  it('describes the audio capabilities as fal-only', () => {
    expect(CAPABILITY_HINTS.MusicGeneration).not.toMatch(/ElevenLabs directly/);
    expect(CAPABILITY_HINTS.SoundEffectGeneration).not.toMatch(/ElevenLabs directly/);
    expect(CAPABILITY_HINTS.AudioIsolation).toMatch(/fal\.ai only/);
  });
});
