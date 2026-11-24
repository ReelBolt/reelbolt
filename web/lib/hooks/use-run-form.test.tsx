// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';

const env = vi.hoisted(() => ({
  cap: null as number | null,
  speech: undefined as unknown,
  speechCalls: 0,
}));
vi.mock('@/lib/hooks/use-styles', () => ({
  useStylePresets: () => ({
    data: [{ key: 'tiktok', name: 'TikTok', description: '', config: {}, suggestedFormat: 'Vertical' }],
  }),
  useEditingStyles: () => ({ data: [] }),
}));
vi.mock('@/lib/hooks/use-billing', () => ({ useOutputHeightCap: () => env.cap }));
vi.mock('@/lib/api/inference-providers', () => ({
  getProviderStatus: () => { env.speechCalls += 1; return Promise.resolve(env.speech); },
}));

import { useRunForm } from './use-run-form';
import { describeVoiceProviderStatus } from '@/components/workflows/VoiceProviderStatusNotice';

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

beforeEach(() => {
  env.cap = null;
  env.speech = undefined;
  env.speechCalls = 0;
});
afterEach(cleanup);

const NO_SERVICE = { providerName: null, healthy: false, usingFallback: false };
const FAILING = { providerName: 'Voices Inc', healthy: false, usingFallback: false };
const FALLBACK = { providerName: 'Backup voices', healthy: true, usingFallback: true };
const NARRATED = [{ stepType: 'Voiceover' as const }];

/** Lets SWR deliver a resolved provider status and the hook re-render with it. */
const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 0)); });

describe('useRunForm', () => {
  it('starts as a plain run: no choices, no brief fields', () => {
    const { result } = renderHook(() => useRunForm({ projectId: 'p1' }), { wrapper });
    expect(result.current.buildChoices()).toEqual({});
    expect(result.current.buildBrief()).toEqual({});
    expect(result.current.trimmedNotes).toBeNull();
  });

  it('lets a preset suggest the shape until the user picks one', () => {
    const { result } = renderHook(() => useRunForm({ projectId: 'p1' }), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    expect(result.current.format).toBe('Vertical');
    act(() => result.current.changeFormat('Square'));
    act(() => result.current.changeStyle('none'));
    act(() => result.current.changeStyle('preset:tiktok'));
    expect(result.current.format).toBe('Square');
  });

  it('follows non-vertical clips with "As set up" instead of the style\'s vertical, and an explicit pick wins', () => {
    const { result } = renderHook(() => useRunForm({ projectId: 'p1', clipsNotVertical: true }), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    expect(result.current.format).toBe('Original');
    expect(result.current.formatFollowsClips).toBe(true);
    expect(result.current.buildBrief()).toEqual({ stylePreset: 'tiktok' });
    act(() => result.current.changeFormat('Vertical'));
    expect(result.current.format).toBe('Vertical');
    expect(result.current.formatFollowsClips).toBe(false);
  });

  it('keeps the style\'s vertical when the clips are vertical or unprobed', () => {
    const { result } = renderHook(() => useRunForm({ projectId: 'p1' }), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    expect(result.current.format).toBe('Vertical');
    expect(result.current.formatFollowsClips).toBe(false);
  });

  it('builds the legacy choices or the typed brief from the same state (a caller uses one, never both)', () => {
    const { result } = renderHook(() => useRunForm({ projectId: 'p1' }), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    act(() => result.current.setResolution('1080p'));
    act(() => result.current.setNotes('  make it punchy  '));
    expect(result.current.buildChoices()).toEqual({
      stylePresetKey: 'tiktok', outputFormat: 'Vertical', outputResolution: '1080p',
    });
    expect(result.current.buildBrief({ lengthSec: 30 })).toEqual({
      lengthSec: 30, stylePreset: 'tiktok', format: 'Vertical', resolution: '1080p', notes: 'make it punchy',
    });
    expect(result.current.trimmedNotes).toBe('make it punchy');
  });

  it('falls back to Auto when the plan does not cover the picked resolution', () => {
    env.cap = 720;
    const { result } = renderHook(() => useRunForm({ projectId: 'p1' }), { wrapper });
    act(() => result.current.setResolution('2160p'));
    expect(result.current.resolution).toBe('Auto');
    expect(result.current.outputResolution).toBeUndefined();
    expect(result.current.buildChoices()).toEqual({});
  });

  it('requires a brief only when asked to, and treats whitespace as empty', () => {
    const { result } = renderHook(() => useRunForm({ requiresBrief: true }), { wrapper });
    expect(result.current.missingBrief).toBe(true);
    act(() => result.current.setNotes('   '));
    expect(result.current.missingBrief).toBe(true);
    act(() => result.current.setNotes('a teaser'));
    expect(result.current.missingBrief).toBe(false);
  });

  it('flags a silent video run, and not a workflow with a voiceover or no film', () => {
    const author = { stepType: 'Agent' as const, agentType: 'AuthorAgent' };
    const silent = renderHook(() => useRunForm({ steps: [author] }), { wrapper });
    expect(silent.result.current.silentVideo).toBe(true);
    const narrated = renderHook(() => useRunForm({ steps: [author, { stepType: 'Voiceover' as const }] }), { wrapper });
    expect(narrated.result.current.silentVideo).toBe(false);
    const none = renderHook(() => useRunForm({ steps: [] }), { wrapper });
    expect(none.result.current.silentVideo).toBe(false);
    expect(none.result.current.speechNotice).toBeNull();
  });

  it('reset returns every choice to its start', () => {
    const { result } = renderHook(() => useRunForm({}), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    act(() => result.current.setNotes('x'));
    act(() => result.current.reset());
    expect(result.current.buildChoices()).toEqual({});
    expect(result.current.notes).toBe('');
  });

  it('never lets buildBrief carry two style choices, the form winning over extra', () => {
    const { result } = renderHook(() => useRunForm({}), { wrapper });
    act(() => result.current.changeStyle('preset:tiktok'));
    const brief = result.current.buildBrief({ styleId: 's1', style: { tone: 'Calm' }, notes: 'old' });
    expect(brief.stylePreset).toBe('tiktok');
    expect(brief.styleId).toBeUndefined();
    expect(brief.style).toBeUndefined();
    const { result: plain } = renderHook(() => useRunForm({}), { wrapper });
    const kept = plain.current.buildBrief({ styleId: 's1', stylePreset: 'x', style: { tone: 'Calm' } });
    expect(Object.keys(kept).filter((k) => ['styleId', 'stylePreset', 'style'].includes(k))).toEqual(['styleId']);
    expect(plain.current.buildBrief({ notes: 'keep' }).notes).toBe('keep');
  });

  it('reports no service as an error when the project has no selectable voice (the run dialog loads none)', async () => {
    env.speech = NO_SERVICE;
    // No `projectVoiceCount`: the run dialog's behaviour must not change.
    const { result } = renderHook(() => useRunForm({ steps: NARRATED }), { wrapper });
    await waitFor(() => expect(result.current.speechNotice?.reason).toBe('noService'));
    expect(result.current.speechNotice?.level).toBe('error');
  });

  it('drops the no-service notice when the project has a selectable voice', async () => {
    env.speech = NO_SERVICE;
    const { result } = renderHook(() => useRunForm({ steps: NARRATED, projectVoiceCount: 1 }), { wrapper });
    await waitFor(() => expect(env.speechCalls).toBeGreaterThan(0));
    await flush();
    expect(result.current.speechNotice).toBeNull();
  });

  it('still surfaces a failing provider and a fallback when the project has a voice', async () => {
    env.speech = FAILING;
    const failing = renderHook(() => useRunForm({ steps: NARRATED, projectVoiceCount: 1 }), { wrapper });
    await waitFor(() => expect(failing.result.current.speechNotice?.reason).toBe('failing'));
    expect(failing.result.current.speechNotice?.level).toBe('error');

    env.speech = FALLBACK;
    const fallback = renderHook(() => useRunForm({ steps: NARRATED, projectVoiceCount: 1 }), { wrapper });
    await waitFor(() => expect(fallback.result.current.speechNotice?.reason).toBe('fallback'));
    expect(fallback.result.current.speechNotice?.level).toBe('warning');
  });
});

describe('describeVoiceProviderStatus reasons', () => {
  const base = {
    capability: 'SpeechSynthesis' as const,
    providerName: 'Voices Inc',
    lastTestOk: true,
    lastTestAt: null,
    healthy: true,
    usingFallback: false,
    message: null,
  };

  it('pins noService vs failing vs fallback, and suppresses only noService for a project voice', () => {
    expect(describeVoiceProviderStatus({ ...base, providerName: null, healthy: false })?.reason).toBe('noService');
    expect(describeVoiceProviderStatus({ ...base, healthy: false })?.reason).toBe('failing');
    expect(describeVoiceProviderStatus({ ...base, usingFallback: true })?.reason).toBe('fallback');
    expect(describeVoiceProviderStatus({ ...base, providerName: null, healthy: false }, undefined, { projectVoiceCount: 1 })).toBeNull();
    expect(describeVoiceProviderStatus({ ...base, healthy: false }, undefined, { projectVoiceCount: 1 })?.reason).toBe('failing');
    expect(describeVoiceProviderStatus({ ...base, usingFallback: true }, undefined, { projectVoiceCount: 1 })?.reason).toBe('fallback');
  });
});
