// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', () => 0);
vi.stubGlobal('cancelAnimationFrame', () => {});
vi.stubGlobal('matchMedia', (q: string) => ({ matches: false, media: q, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} }));

import { SubjectPanel } from './SubjectPanel';

afterEach(cleanup);

const base = {
  points: [],
  boxes: [],
  blocker: null,
  busy: false,
  onRemovePoint: () => {},
  onRemoveBox: () => {},
  onClear: () => {},
  onRun: () => {},
};

describe('SubjectPanel leave-out choice (PE-069b)', () => {
  it('offers "Leave this out" so a touch screen can place a leave-out mark without Shift', () => {
    const onMarkMode = vi.fn();
    render(
      <MantineProvider env="test">
        <SubjectPanel {...base} markMode="include" onMarkMode={onMarkMode} />
      </MantineProvider>,
    );
    expect(screen.getByRole('radiogroup', { name: 'What a click marks' })).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Leave this out'));
    expect(onMarkMode).toHaveBeenCalledWith('leaveOut');
  });
});
