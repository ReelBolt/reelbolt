import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useViewerFullscreen } from '../useViewerFullscreen';

/** The viewer on the whole screen: the Fullscreen API on the viewer zone, view state only. */

function Host({ onPortalTarget }: { onPortalTarget?: (target: HTMLElement | undefined) => void }) {
  const { attach, active, supported, toggle, exit, portalTheme } = useViewerFullscreen();
  onPortalTarget?.(portalTheme.components?.Portal.defaultProps.target);
  return (
    <div ref={attach} data-testid="zone" data-active={active} data-supported={supported}>
      <button type="button" onClick={toggle}>toggle</button>
      <button type="button" onClick={exit}>exit</button>
    </div>
  );
}

function stubFullscreen(enabled: boolean) {
  let current: Element | null = null;
  const enter = (element: Element) => {
    current = element;
    document.dispatchEvent(new Event('fullscreenchange'));
    return Promise.resolve();
  };
  const request = vi.fn(function (this: Element) {
    return enter(this);
  });
  const exit = vi.fn(() => {
    current = null;
    document.dispatchEvent(new Event('fullscreenchange'));
    return Promise.resolve();
  });
  Object.defineProperty(document, 'fullscreenEnabled', { configurable: true, get: () => enabled });
  Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => current });
  Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: exit });
  Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', { configurable: true, value: request });
  return { request, exit, leave: () => exit() };
}

afterEach(() => {
  for (const key of ['fullscreenEnabled', 'fullscreenElement', 'exitFullscreen'] as const) delete (document as unknown as Record<string, unknown>)[key];
  delete (HTMLElement.prototype as unknown as Record<string, unknown>).requestFullscreen;
});

describe('useViewerFullscreen', () => {
  it('puts the viewer zone on the whole screen and back, and reports it', () => {
    const api = stubFullscreen(true);
    render(<Host />);
    const zone = screen.getByTestId('zone');
    expect(zone.dataset.supported).toBe('true');
    expect(zone.dataset.active).toBe('false');
    act(() => fireEvent.click(screen.getByText('toggle')));
    expect(api.request).toHaveBeenCalledTimes(1);
    expect(zone.dataset.active).toBe('true');
    act(() => fireEvent.click(screen.getByText('toggle')));
    expect(api.exit).toHaveBeenCalledTimes(1);
    expect(zone.dataset.active).toBe('false');
  });

  it('follows the browser: Escape (the browser leaving full screen by itself) turns it off', () => {
    const api = stubFullscreen(true);
    render(<Host />);
    act(() => fireEvent.click(screen.getByText('toggle')));
    expect(screen.getByTestId('zone').dataset.active).toBe('true');
    act(() => void api.leave());
    expect(screen.getByTestId('zone').dataset.active).toBe('false');
  });

  it('while full screen, menus and tooltips of the viewer mount inside the zone (nothing outside it is shown)', () => {
    stubFullscreen(true);
    const targets: (HTMLElement | undefined)[] = [];
    render(<Host onPortalTarget={(t) => targets.push(t)} />);
    expect(targets.at(-1)).toBeUndefined();
    act(() => fireEvent.click(screen.getByText('toggle')));
    expect(targets.at(-1)).toBe(screen.getByTestId('zone'));
  });

  it('exit is a no-op when not full screen, and leaves it when on', () => {
    const api = stubFullscreen(true);
    render(<Host />);
    act(() => fireEvent.click(screen.getByText('exit')));
    expect(api.exit).not.toHaveBeenCalled();
    act(() => fireEvent.click(screen.getByText('toggle')));
    act(() => fireEvent.click(screen.getByText('exit')));
    expect(api.exit).toHaveBeenCalledTimes(1);
  });

  it('a browser without the API (an iPhone) is not offered it and toggling does nothing', () => {
    const api = stubFullscreen(false);
    render(<Host />);
    expect(screen.getByTestId('zone').dataset.supported).toBe('false');
    act(() => fireEvent.click(screen.getByText('toggle')));
    expect(api.request).not.toHaveBeenCalled();
  });
});
