import { act } from '@testing-library/react';
import { resetFullViewPreferenceCache } from '../use-device-class';

const SIZES = { phone: [390, 844], tablet: [820, 1180], desktop: [1440, 900] } as const;

/** Sets the jsdom window to a phone, tablet or desktop viewport and tells `useDeviceClass` (resize event). */
export function setTestViewport(kind: keyof typeof SIZES): void {
  const [w, h] = SIZES[kind];
  Object.defineProperty(window, 'innerWidth', { value: w, configurable: true, writable: true });
  Object.defineProperty(window, 'innerHeight', { value: h, configurable: true, writable: true });
  act(() => {
    window.dispatchEvent(new Event('resize'));
  });
}

export function resetTestViewport(): void {
  resetFullViewPreferenceCache();
  setTestViewport('desktop');
}
