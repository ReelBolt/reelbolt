// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { createEditorStore } from '@/lib/editing/store';
import { doc, media, track, TRACKS, assetRef } from '@/lib/editing/__tests__/fixtures';
import type { EditOp } from '@/lib/editing/engine';
import type { MediaClip } from '@/lib/editing/types';
import type { MediaProbe } from '@/lib/editing/media/types';
import { PROBE_FIXTURE } from './camera-profile-fixtures';
import { CameraProfileControl, CameraProfileRow, resetDismissedSuggestions } from '../color/CameraProfileControl';
import { ViewingLutBadge, ViewingLutMenu } from '../viewer/ViewingLutMenu';
import { colorCameraRows } from '../media/MetadataPanel';
import { markAutoAppliedForTest } from './camera-profile-fixtures';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [{ id: 'f1', originalFileName: 'look.cube' }, { id: 'f2', originalFileName: 'notes.txt' }] }),
  useProjectFileContent: () => ({ data: undefined, error: undefined }),
}));

vi.stubGlobal(
  'ResizeObserver',
  class {
    observe() {}
    unobserve() {}
    disconnect() {}
  },
);

afterEach(() => {
  cleanup();
  resetDismissedSuggestions();
});

const clipIn = (id: string, extra: Partial<MediaClip> = {}) => media(id, 0, 60, 0, extra);
function harness(clipExtra: Partial<MediaClip> = {}, probe: MediaProbe | null = null, locale = 'en') {
  const d = doc([track(TRACKS.v1, 'video', [clipIn('a', { ...clipExtra }), media('b', 60, 120, 0, { assetRef: assetRef(1) }), media('c', 120, 180, 0, { assetRef: assetRef(2) })])]);
  const store = createEditorStore({ timelineId: 't', projectId: 'p', doc: d, revision: 1 });
  const sets: Array<[string | null, string]> = [];
  const batches: EditOp[][] = [];
  const clip = () => store.getState().doc.tracks[0].items[0] as MediaClip;
  const ui = (
    <CameraProfileControl clip={clip()} doc={store.getState().doc} readOnly={false} probe={probe} onSet={(p, l) => sets.push([p, l])} onBatch={(ops) => batches.push(ops)} />
  );
  renderWithI18n(ui, { locale });
  return { sets, batches };
}

describe('camera profile control', () => {
  it('lists None and the 12 profiles grouped by maker, with the uncalibrated note', () => {
    harness({ inputProfile: 'appleLog' });
    expect(screen.getByTestId('camera-profile-note').textContent).toContain('not yet checked on real footage');
    fireEvent.click(screen.getByTestId('camera-profile-select'));
    expect(screen.getAllByRole('option').length).toBe(13);
  });

  it('shows the automatic badge with Turn off, which sets null', () => {
    markAutoAppliedForTest('a');
    const { sets } = harness({ inputProfile: 'appleLog' });
    expect(screen.getByTestId('camera-profile-badge').textContent).toContain('Converted from Apple Log automatically');
    expect(screen.getByTestId('camera-profile-badge').textContent).toContain('Wrong? Pick another profile or None.');
    fireEvent.click(screen.getByTestId('camera-profile-turn-off'));
    expect(sets).toEqual([[null, 'Camera profile off']]);
  });

  it('reads "Camera profile: X" after a reload (not auto-applied in this session)', () => {
    harness({ inputProfile: 'djiDLog' });
    expect(screen.getByTestId('camera-profile-badge').textContent).toContain('Camera profile: ');
  });

  it('a medium hint offers Convert and a session-local Not this one', () => {
    const probe = { ...PROBE_FIXTURE, cameraProfileHint: { profile: 'sonySLog3', confidence: 'medium' as const, reason: 'x' } };
    const { sets } = harness({}, probe);
    expect(screen.getByTestId('camera-profile-chip').textContent).toContain('Looks like S-Log3');
    fireEvent.click(screen.getByTestId('camera-profile-chip-dismiss'));
    expect(screen.queryByTestId('camera-profile-chip')).toBeNull();
    expect(sets).toEqual([]);
  });

  it('Convert on the chip sets the suggested profile', () => {
    const probe = { ...PROBE_FIXTURE, cameraProfileHint: { profile: 'panasonicVLog', confidence: 'medium' as const, reason: 'x' } };
    const { sets } = harness({}, probe);
    fireEvent.click(screen.getByTestId('camera-profile-chip-convert'));
    expect(sets[0][0]).toBe('panasonicVLog');
  });

  it('Apply to all clips from this file is one batch over the other clips of the same asset', () => {
    const { batches } = harness({ inputProfile: 'appleLog' });
    fireEvent.click(screen.getByTestId('camera-profile-apply-all'));
    expect(batches).toHaveLength(1);
    expect(batches[0]).toEqual([{ type: 'setProperty', itemId: 'b', key: 'inputProfile', value: 'appleLog' }]);
  });

  it('warns about a double conversion when the clip has a profile and a LUT node', () => {
    const grade = { nodes: [{ id: 'n', name: 'LUT', enabled: true, lut: { file: assetRef(9) } }] } as unknown as MediaClip['grade'];
    harness({ inputProfile: 'appleLog', grade });
    expect(screen.getByTestId('camera-profile-double-hint').textContent).toContain('not converted twice');
  });

  it('Italian strings are present', () => {
    harness({ inputProfile: 'appleLog' }, null, 'it');
    expect(screen.getByTestId('camera-profile-badge').textContent).toContain('Profilo camera: ');
    expect(screen.getByTestId('camera-profile-turn-off').textContent).toBe('Disattiva');
  });
});

describe('viewing LUT control', () => {
  it('lists Off, project .cube files only, and camera profiles as a view', async () => {
    const picks: unknown[] = [];
    renderWithI18n(<ViewingLutMenu projectId="p" choice={null} onChoose={(c) => picks.push(c)} problem={null} />, { locale: 'en' });
    fireEvent.click(screen.getByTestId('viewing-lut-button'));
    expect(await screen.findByTestId('viewing-lut-off')).toBeTruthy();
    expect(screen.getByTestId('viewing-lut-file-f1').textContent).toBe('look.cube');
    expect(screen.queryByTestId('viewing-lut-file-f2')).toBeNull();
    fireEvent.click(screen.getByTestId('viewing-lut-profile-appleLog'));
    expect(picks).toEqual([{ kind: 'profile', id: 'appleLog' }]);
  });

  it('shows a plain message for a refused file', () => {
    renderWithI18n(<ViewingLutMenu projectId="p" choice={null} onChoose={() => {}} problem="too_large" />, { locale: 'en' });
    expect(screen.getByTestId('viewing-lut-problem').textContent).toContain('The viewing LUT is off');
  });

  it('the permanent badge says it is preview only, in English and Italian', () => {
    renderWithI18n(<ViewingLutBadge />, { locale: 'en' });
    expect(screen.getByTestId('viewing-lut-badge').textContent).toBe('Viewing LUT on. Preview only, not in your export.');
    cleanup();
    renderWithI18n(<ViewingLutBadge />, { locale: 'it' });
    expect(screen.getByTestId('viewing-lut-badge').textContent).toContain('Solo anteprima');
  });
});

describe('media metadata colour rows', () => {
  const tc = ((key: string, values?: Record<string, unknown>) => (values ? `${key}:${JSON.stringify(values)}` : key)) as never;
  it('shows only known facts and the detected profile', () => {
    const rows = colorCameraRows(
      { mediaRef: 'x', kind: 'video', fileName: 'a', mimeType: 'v', sizeBytes: 1, category: 'userFiles', bins: [], filmstripUrl: '', probe: { ...PROBE_FIXTURE, colorTransfer: 'arib-std-b67', make: 'Apple', model: null, bitDepth: 10, cameraProfileHint: { profile: 'appleLog', confidence: 'medium', reason: 'x' } } },
      tc,
    );
    expect(rows.map((r) => r.key)).toEqual(['colorTransfer', 'bitDepth', 'make', 'detected']);
    expect(rows[3].value).toContain('Apple Log');
  });
});

describe('camera profile row on the phone surface', () => {
  it('is 44 px tall in touch mode', () => {
    const d = doc([track(TRACKS.v1, 'video', [clipIn('a')])]);
    const clip = d.tracks[0].items[0] as MediaClip;
    renderWithI18n(<CameraProfileRow clip={clip} readOnly={false} touch onSet={() => null} testIdPrefix="t" />, { locale: 'en' });
    const input = screen.getByTestId('t-row').querySelector('input.mantine-Select-input') as HTMLElement;
    expect(input.style.height).toBe('44px');
  });
});
