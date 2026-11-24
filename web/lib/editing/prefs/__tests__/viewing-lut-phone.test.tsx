// @vitest-environment jsdom
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';

const deviceClass = vi.hoisted(() => ({ value: 'desktop' as 'desktop' | 'phone' }));
vi.mock('../../use-device-class', () => ({ useDeviceClass: () => deviceClass.value }));

import { useViewingLut, writeViewingLut } from '../viewing-lut';

function Probe() {
  return <span data-testid="seen">{JSON.stringify(useViewingLut('t-phone').choice)}</span>;
}

afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

describe('viewing LUT on the phone surface', () => {
  it('is on for a desktop and always off on a phone, leaving the stored choice alone', () => {
    writeViewingLut('t-phone', { kind: 'profile', id: 'appleLog' });
    deviceClass.value = 'desktop';
    render(<Probe />);
    expect(screen.getByTestId('seen').textContent).toBe(JSON.stringify({ kind: 'profile', id: 'appleLog' }));
    cleanup();
    deviceClass.value = 'phone';
    act(() => {
      render(<Probe />);
    });
    expect(screen.getByTestId('seen').textContent).toBe('null');
    expect(window.localStorage.getItem('reelbolt:editor:viewing-lut:t-phone')).not.toBeNull();
  });
});
