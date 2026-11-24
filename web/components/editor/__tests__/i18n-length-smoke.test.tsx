// @vitest-environment jsdom
/**
 * One-screen length smoke: the phone editor chrome (header, dock) and the Deliver panel (settings and queue)
 * render in Italian and in the accented, padded pseudo-locale without throwing, and every control the person
 * taps keeps an accessible name (no empty labels).
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { PhoneHeader } from '../phone/PhoneHeader';
import { PhoneDock } from '../phone/PhoneDock';
import { RenderSettings } from '../deliver/RenderSettings';
import { RenderQueue } from '../deliver/RenderQueue';

global.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver;
Element.prototype.scrollIntoView = vi.fn();
afterEach(() => cleanup());

const headerProps = {
  name: 'My film',
  save: { status: 'saved' } as never,
  backHref: '/editor/p',
  reviewHref: '/review',
  canUndo: true,
  canRedo: false,
  onUndo: vi.fn(),
  onRedo: vi.fn(),
  onExport: vi.fn(),
  onOpenSheet: vi.fn(),
  touchDrag: false,
  onToggleTouchDrag: vi.fn(),
};

/** A control's accessible name: aria-label, aria-labelledby target, or its text. */
function nameOf(el: Element): string {
  const label = el.getAttribute('aria-label');
  if (label) return label.trim();
  const by = el.getAttribute('aria-labelledby');
  if (by) return by.split(' ').map((id) => document.getElementById(id)?.textContent ?? '').join(' ').trim();
  const id = el.getAttribute('id');
  const forLabel = id ? document.querySelector(`label[for="${id}"]`) : null;
  return (forLabel?.textContent ?? el.textContent ?? '').trim();
}

function expectNamedControls(container: HTMLElement): void {
  const controls = container.querySelectorAll('button, a[href], input:not([type="hidden"]), select, textarea, [role="button"], [role="switch"], [role="combobox"]');
  expect(controls.length).toBeGreaterThan(0);
  const unnamed = [...controls].filter((el) => nameOf(el) === '' && !el.closest('[aria-hidden="true"]')).map((el) => el.outerHTML.slice(0, 120));
  expect(unnamed).toEqual([]);
}

describe.each(['it', 'en-XA'])('editor length smoke in %s', (locale) => {
  it('renders the phone editor shell with named controls', () => {
    const { container } = renderWithI18n(
      <div>
        <PhoneHeader {...headerProps} />
        <PhoneDock active={null} onSelect={() => undefined} />
      </div>,
      { locale },
    );
    expect(screen.getByTestId('phone-export')).toBeTruthy();
    expectNamedControls(container);
  });

  it('renders the Deliver panel with named controls', () => {
    const { container } = renderWithI18n(
      <div>
        <RenderSettings inOut={{ inFrame: 10, outFrame: 40 }} revision={5} readOnly={false} busy={false} onRender={vi.fn()} />
        <RenderQueue renders={[]} progress={null} outputUrl={() => ''} onCancel={() => undefined} loading={false} />
      </div>,
      { locale },
    );
    expect(screen.getByTestId('render-start')).toBeTruthy();
    expectNamedControls(container);
  });
});

describe('short phone labels stay short in Italian', () => {
  const flat = (d: Record<string, unknown>, p = ''): Array<[string, string]> =>
    Object.entries(d).flatMap(([k, v]) => (typeof v === 'object' && v !== null ? flat(v as Record<string, unknown>, `${p}${k}.`) : typeof v === 'string' ? [[`${p}${k}`, v] as [string, string]] : []));
  it('no short English phone label is more than about 2.4 times as long in Italian', async () => {
    const en = Object.fromEntries(flat((await import('../../../messages/en/editor.json')).default.phone as never));
    const it = Object.fromEntries(flat((await import('../../../messages/it/editor.json')).default.phone as never));
    const long = Object.keys(en).filter((k) => !en[k].includes('{') && en[k].length <= 22 && it[k].length > Math.max(2.4 * en[k].length, 24));
    expect(long.map((k) => `${k}: ${en[k]} / ${it[k]}`)).toEqual([]);
  });
});
