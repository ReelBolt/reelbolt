// @vitest-environment jsdom
/**
 * W0.6 (UI2-09): a global editor shortcut does not fire behind an open dialog, inside a dialog, on a
 * slider or other keyboard-owning control, in a text field, or as Space/Enter on a focused button.
 */
import { afterEach, describe, expect, it } from 'vitest';
import { shortcutBlocked } from '../keymap';

afterEach(() => {
  document.body.innerHTML = '';
});

const el = (html: string): HTMLElement => {
  const host = document.createElement('div');
  host.innerHTML = html;
  document.body.appendChild(host);
  return host.firstElementChild as HTMLElement;
};

describe('shortcutBlocked', () => {
  it('lets a shortcut through from the page body', () => {
    expect(shortcutBlocked(document.body, 'Delete')).toBe(false);
    expect(shortcutBlocked(null, 'Space')).toBe(false);
  });

  it('blocks text fields and keyboard-owning controls', () => {
    expect(shortcutBlocked(el('<input />'), 'J')).toBe(true);
    expect(shortcutBlocked(el('<textarea></textarea>'), 'J')).toBe(true);
    expect(shortcutBlocked(el('<select></select>'), 'J')).toBe(true);
    expect(shortcutBlocked(el('<div role="slider" tabindex="0"></div>'), 'ArrowRight')).toBe(true);
    expect(shortcutBlocked(el('<div role="menuitem" tabindex="0"></div>'), 'ArrowDown')).toBe(true);
  });

  it('blocks Space and Enter on a focused button, but not other keys', () => {
    const button = el('<button>Snap</button>');
    expect(shortcutBlocked(button, 'Space')).toBe(true);
    expect(shortcutBlocked(button, 'Enter')).toBe(true);
    expect(shortcutBlocked(button, 'J')).toBe(false);
  });

  it('lets shortcuts through from a clicked panel tab, except the keys the tab list uses itself', () => {
    const tab = el('<button role="tab" aria-selected="true">Inspector</button>');
    expect(shortcutBlocked(tab, 'J')).toBe(false);
    expect(shortcutBlocked(tab, 'Delete')).toBe(false);
    expect(shortcutBlocked(tab, 'Mod+Z')).toBe(false);
    expect(shortcutBlocked(tab, 'ArrowRight')).toBe(true);
    expect(shortcutBlocked(tab, 'Home')).toBe(true);
    expect(shortcutBlocked(tab, 'Space')).toBe(true);
    expect(shortcutBlocked(tab, 'Enter')).toBe(true);
  });

  it('blocks everything inside a dialog and while a modal dialog is open', () => {
    const inside = el('<div role="dialog"><button id="b">x</button></div>').querySelector('#b') as HTMLElement;
    expect(shortcutBlocked(inside, 'Mod+Z')).toBe(true);
    document.body.innerHTML = '';
    el('<section role="dialog" aria-modal="true"><p>Keyboard shortcuts</p></section>');
    expect(shortcutBlocked(document.body, 'Delete')).toBe(true);
  });
});
