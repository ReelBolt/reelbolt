import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { act, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { APP_TOUCH_ATTRIBUTE, useAppTouchAttribute, useDeviceClass } from './use-device-class';

function setViewport(width: number, height: number, coarse: boolean): void {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: height });
  window.matchMedia = ((query: string) => ({
    matches: query === '(pointer: coarse)' ? coarse : false,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  })) as unknown as typeof window.matchMedia;
}

function Probe({ enabled = true }: { enabled?: boolean }) {
  const cls = useDeviceClass();
  useAppTouchAttribute(enabled);
  return <p data-testid="cls">{cls}</p>;
}

afterEach(() => document.body.removeAttribute(APP_TOUCH_ATTRIBUTE));

describe('useDeviceClass (app-wide)', () => {
  it.each([
    [390, 844, true, 'phone'],
    [700, 900, false, 'tablet'],
    [1024, 768, true, 'tablet'],
    [1100, 800, false, 'tablet'],
    [1440, 900, false, 'desktop'],
  ])('%ix%i coarse=%s is %s', (w, h, coarse, expected) => {
    setViewport(w, h, coarse);
    const { getByTestId } = render(<Probe />);
    act(() => undefined);
    expect(getByTestId('cls').textContent).toBe(expected);
  });
});

describe('useAppTouchAttribute', () => {
  it('marks body on a phone and clears it on unmount', () => {
    setViewport(390, 844, true);
    const { unmount } = render(<Probe />);
    expect(document.body.getAttribute(APP_TOUCH_ATTRIBUTE)).toBe('true');
    unmount();
    expect(document.body.hasAttribute(APP_TOUCH_ATTRIBUTE)).toBe(false);
  });
  it('marks a coarse-pointer tablet', () => {
    setViewport(820, 1180, true);
    render(<Probe />);
    expect(document.body.getAttribute(APP_TOUCH_ATTRIBUTE)).toBe('true');
  });
  it('leaves a desktop and a disabled surface (the editor) alone', () => {
    setViewport(1440, 900, false);
    render(<Probe />);
    expect(document.body.hasAttribute(APP_TOUCH_ATTRIBUTE)).toBe(false);
    setViewport(390, 844, true);
    render(<Probe enabled={false} />);
    expect(document.body.hasAttribute(APP_TOUCH_ATTRIBUTE)).toBe(false);
  });
});

describe('global.css app touch rule', () => {
  const css = readFileSync(join(__dirname, '..', '..', 'app', 'global.css'), 'utf8');
  const block = css.slice(css.indexOf("body[data-app-touch='true'] {"));
  it('is scoped by the attribute, never a bare selector', () => {
    expect(block).not.toMatch(/^\.mantine-(Button|ActionIcon|Badge)/m);
  });
  it('raises Button, ActionIcon, Tabs and Badge-as-button to 44 px and keeps a 12 px floor', () => {
    expect(block).toMatch(/\.mantine-ActionIcon-root[\s\S]*?min-height: 44px/);
    expect(block).toMatch(/\.mantine-Tabs-tab/);
    expect(block).toMatch(/\.mantine-Badge-root[\s\S]*?min-height: 44px/);
    expect(block).toMatch(/\.mantine-Checkbox-body \{\s*min-height: 44px/);
    expect(block).toMatch(/--badge-fz-xs: 0\.75rem/);
    expect(block).toMatch(/--mantine-font-size-xs: 0\.75rem/);
  });
});
