// @vitest-environment jsdom
/** Round 2 (T-004): translated preview wording, the phone transport keeps Play in a fixed cluster, 44 px preview chip, translated link banner. */
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { useT } from '@/i18n';
import { previewText } from '../preview-text';
import { autoPlanText } from '@/lib/editing/preview/labels-msg';
import { REASON_NO_WEBGL2 } from '@/lib/editing/preview/fault-reasons';
import { TransportBar } from '../TransportBar';
import { RenderRequiredBadge } from '../RenderRequiredBadge';
import { setTestViewport, resetTestViewport } from '@/lib/editing/__tests__/viewport';

afterEach(() => {
  cleanup();
  resetTestViewport();
});

function Text({ s }: { s: string }) {
  const t = useT();
  return <p data-testid="out">{previewText(s, t.msg)}</p>;
}
const say = (s: string, locale: string): string => {
  renderWithI18n(<Text s={s} />, { locale });
  const out = screen.getByTestId('out').textContent ?? '';
  cleanup();
  return out;
};

describe('previewText', () => {
  it('leaves English as it is', () => {
    expect(say('Preview faithful', 'en')).toBe('Preview faithful');
  });
  it('translates the chip labels and the software-graphics note in Italian', () => {
    expect(say('Preview faithful', 'it')).toBe('Anteprima fedele');
    expect(say('Preview not ready', 'it')).not.toMatch(/Preview/);
    expect(say('Approximate preview (2)', 'it')).not.toMatch(/Approximate/);
    expect(say('This browser draws with software graphics, so the preview is scaled down to keep up.', 'it')).not.toMatch(/graphics/);
  });
  it('translates the wrapper and the noun of a line but keeps the clip name', () => {
    const out = say('Clip "My film": This clip has no proxy yet, so the preview reads its original file, which may stutter or fail to open.', 'it');
    expect(out).toContain('"My film"');
    expect(out).not.toMatch(/proxy yet/);
    expect(say('Reduced quality: This browser draws with software graphics, so the preview is scaled down to keep up.', 'it')).not.toMatch(/Reduced|graphics/);
  });
  it('translates a fault reason and the Auto sentence in Italian', () => {
    expect(say(REASON_NO_WEBGL2, 'it')).not.toMatch(/graphics acceleration|live preview/);
    expect(say(`Reduced quality: ${REASON_NO_WEBGL2}`, 'it')).not.toMatch(/Reduced|graphics acceleration/);
    function Auto() {
      const t = useT();
      return <p data-testid="out">{autoPlanText({ autoReason: 'x', autoWhy: { mode: 'reduced', codes: [{ code: 'cores', n: 2 }] } }, t.msg, 'it')}</p>;
    }
    renderWithI18n(<Auto />, { locale: 'it' });
    expect(screen.getByTestId('out').textContent).toMatch(/^L'anteprima è disegnata a una dimensione ridotta perché /);
  });
  it('passes unknown text through', () => {
    expect(say('Something else', 'it')).toBe('Something else');
  });
});

const baseTransport = {
  playheadFrame: 0, rate: { num: 30, den: 1 }, endFrame: 300, speed: 0 as const, loop: false, inFrame: null, outFrame: null,
  onSeek: vi.fn(), onSpeed: vi.fn(), onStep: vi.fn(), onPrevEdit: vi.fn(), onNextEdit: vi.fn(), onPrevMarker: vi.fn(), onNextMarker: vi.fn(),
  onMarkIn: vi.fn(), onMarkOut: vi.fn(), onToggleLoop: vi.fn(), onPlayInToOut: vi.fn(),
};

describe('TransportBar on a phone', () => {
  it('keeps Play and the frame steps in a fixed cluster and scrolls the rest inside its own region', () => {
    renderWithI18n(<TransportBar {...baseTransport} compact />, { locale: 'en' });
    const core = screen.getByTestId('transport-core');
    const more = screen.getByTestId('transport-more');
    expect(core.contains(screen.getByTestId('transport-play'))).toBe(true);
    expect(core.querySelectorAll('button').length).toBe(3);
    expect(more.contains(screen.getByTestId('timecode-field'))).toBe(true);
    expect(more.style.minWidth).toMatch(/^0(px)?$/);
    expect(more.style.overflowX).toBe('auto');
    expect(screen.getByTestId('transport-bar').style.width).toBe('100%');
  });
  it('keeps the wrapping layout without a core cluster off the phone', () => {
    renderWithI18n(<TransportBar {...baseTransport} />, { locale: 'en' });
    expect(screen.queryByTestId('transport-core')).toBeNull();
    expect(screen.getByTestId('transport-play')).toBeTruthy();
  });
});

describe('preview chip', () => {
  const parity = { worst: 'faithful', approximateCount: 0, renderRequiredCount: 0, items: [] } as never;
  it('is 44 px tall on a phone and 32 px on a desktop', () => {
    setTestViewport('phone');
    renderWithI18n(<RenderRequiredBadge parity={parity} />, { locale: 'en' });
    expect(screen.getByTestId('parity-badge').style.minHeight).toBe('44px');
    cleanup();
    setTestViewport('desktop');
    renderWithI18n(<RenderRequiredBadge parity={parity} />, { locale: 'en' });
    expect(screen.getByTestId('parity-badge').style.minHeight).toBe('32px');
  });
  it('speaks Italian', () => {
    renderWithI18n(<RenderRequiredBadge parity={parity} />, { locale: 'it' });
    expect(screen.getByTestId('parity-badge').textContent).toBe('Anteprima fedele');
  });
});

import { linkSummaryMsg } from '@/lib/editing/summary-msg';

describe('link summary', () => {
  it('has no English left in Italian', () => {
    function S() {
      const t = useT();
      return <p data-testid="s">{t.msg(linkSummaryMsg(0, 2))}</p>;
    }
    renderWithI18n(<S />, { locale: 'it' });
    expect(screen.getByTestId('s').textContent).not.toMatch(/Added/);
  });
});
