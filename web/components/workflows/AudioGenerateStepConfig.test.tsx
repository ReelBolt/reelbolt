// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import {
  AudioGenerateStepConfig,
  createDefaultAudioGenerateStepConfig,
  AUDIO_DURATION_RANGE,
} from './AudioGenerateStepConfig';
import type { AudioGenerateStepConfig as Cfg } from '@/lib/types/workflow';

const seen = vi.hoisted(() => ({ capabilities: [] as (string | null | undefined)[] }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability?: string | null) => {
    seen.capabilities.push(capability);
    return {
      data: [
        { id: 'fal-music', name: 'Music one', capability: 'MusicGeneration', model: 'fal-ai/elevenlabs/music', isPlatformManaged: true, kind: 'FalAi', providerName: 'fal.ai' },
        { id: 'el-sfx', name: 'SFX one', capability: 'SoundEffectGeneration', model: 'eleven_text_to_sound_v2', isPlatformManaged: true, kind: 'ElevenLabs', providerName: 'ElevenLabs' },
      ],
      isLoading: false,
      error: undefined,
    };
  },
}));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => { cleanup(); seen.capabilities = []; });

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <AudioGenerateStepConfig config={{ ...createDefaultAudioGenerateStepConfig(), ...overrides }} onChange={onChange} currentStepIndex={0} />
    </MantineProvider>,
  );
  return onChange;
}
const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('createDefaultAudioGenerateStepConfig', () => {
  it('mirrors the C# record: music, one take, saved to project files, no spend limit and nothing optional set', () => {
    expect(createDefaultAudioGenerateStepConfig()).toEqual({
      kind: 'Music', prompt: '', durationSec: 30, instrumental: null, compositionPlanJson: null, loop: null,
      promptInfluence: null, providerId: null, model: null, takes: 1, maxSpendCredits: null,
      registerProjectFile: true, fileNamePrefix: null,
    });
  });
});

describe('AudioGenerateStepConfig', () => {
  it('filters the provider select by the kind: music rows for Music, sound-effect rows for Sound effect', () => {
    renderConfig();
    expect(seen.capabilities.at(-1)).toBe('MusicGeneration');
    fireEvent.click(screen.getByRole('combobox', { name: /Provider/ }));
    expect(screen.getByRole('option', { name: /Music one/ })).toBeTruthy();
    cleanup();

    renderConfig({ kind: 'SoundEffect', durationSec: 5 });
    expect(seen.capabilities.at(-1)).toBe('SoundEffectGeneration');
  });

  it('shows the music options for Music and the sound-effect options for Sound effect, never both', () => {
    renderConfig();
    expect(screen.getByRole('switch', { name: /Instrumental only/ })).toBeTruthy();
    expect(screen.queryByRole('switch', { name: /seamless loop/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /How closely to follow/ })).toBeNull();
    cleanup();

    renderConfig({ kind: 'SoundEffect', durationSec: 5 });
    expect(screen.queryByRole('switch', { name: /Instrumental only/ })).toBeNull();
    expect(screen.getByRole('switch', { name: /seamless loop/ })).toBeTruthy();
    expect(screen.getByRole('textbox', { name: /How closely to follow/ })).toBeTruthy();
  });

  it('switching kind clears the other kind\'s options and the provider, and fits the length to the new range', () => {
    const onChange = renderConfig({ kind: 'Music', instrumental: true, providerId: 'fal-music', model: 'x', durationSec: 120 });
    fireEvent.click(screen.getByText('Sound effect'));
    expect(last(onChange)).toMatchObject({
      kind: 'SoundEffect', instrumental: null, compositionPlanJson: null, providerId: null, model: null,
      durationSec: AUDIO_DURATION_RANGE.SoundEffect.fallback,
    });
  });

  it('clamps the length to the kind\'s range (music 3-300, sound effect 0.5-30)', () => {
    let onChange = renderConfig();
    const length = screen.getByRole('textbox', { name: /Length \(seconds\)/ });
    fireEvent.change(length, { target: { value: '999' } });
    expect(last(onChange).durationSec).toBe(300);
    fireEvent.change(length, { target: { value: '1' } });
    expect(last(onChange).durationSec).toBe(3);
    cleanup();

    onChange = renderConfig({ kind: 'SoundEffect', durationSec: 5 });
    fireEvent.change(screen.getByRole('textbox', { name: /Length \(seconds\)/ }), { target: { value: '60' } });
    expect(last(onChange).durationSec).toBe(30);
  });

  it('routes the prompt, instrumental, takes (1-4) and a positive max spend; a blank spend limit warns', () => {
    const onChange = renderConfig();
    expect(screen.getByText(/No spending limit is set/)).toBeTruthy();
    fireEvent.change(screen.getByRole('textbox', { name: /Description/ }), { target: { value: 'warm guitar' } });
    expect(last(onChange).prompt).toBe('warm guitar');
    fireEvent.click(screen.getByRole('switch', { name: /Instrumental only/ }));
    expect(last(onChange).instrumental).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /^Takes/ }), { target: { value: '9' } });
    expect(last(onChange).takes).toBe(4);
    fireEvent.change(screen.getByRole('textbox', { name: /Spending limit/ }), { target: { value: '2.5' } });
    expect(last(onChange).maxSpendCredits).toBe(2.5);
  });

  it('drops the warning once a spend limit is set', () => {
    renderConfig({ maxSpendCredits: 3 });
    expect(screen.queryByText(/No spending limit is set/)).toBeNull();
  });

  it('a composition plan replaces the description and the length (the server refuses both with a plan)', async () => {
    const onChange = renderConfig({ prompt: 'warm guitar', instrumental: true });
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /Composition plan/ }), { target: { value: '{"sections":[]}' } });
    expect(last(onChange)).toMatchObject({ compositionPlanJson: '{"sections":[]}', prompt: '', durationSec: 0, instrumental: null });
  });

  it('with a plan set, hides the description and the length', () => {
    renderConfig({ compositionPlanJson: '{"sections":[]}', prompt: '', durationSec: 0 });
    expect(screen.queryByRole('textbox', { name: /Description/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /Length \(seconds\)/ })).toBeNull();
  });

  it('blank model and file prefix become null, not empty strings', async () => {
    const onChange = renderConfig({ model: 'm', fileNamePrefix: 'p' });
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    fireEvent.change(await screen.findByRole('textbox', { name: /^Model/ }), { target: { value: '  ' } });
    expect(last(onChange).model).toBeNull();
    fireEvent.change(screen.getByRole('textbox', { name: /File name prefix/ }), { target: { value: '' } });
    expect(last(onChange).fileNamePrefix).toBeNull();
  });
});
