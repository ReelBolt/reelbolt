// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { HunkView, hunkRows } from './HunkView';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
});
afterEach(cleanup);

const hunk = {
  fromStart: 10, fromCount: 2, toStart: 10, toCount: 2,
  lines: [
    { op: ' ', text: 'keep' },
    { op: '-', text: 'old' },
    { op: '+', text: 'new' },
  ],
};

describe('hunk renderer', () => {
  it('numbers lines on both sides', () => {
    expect(hunkRows(hunk).map((r) => [r.op, r.fromLine, r.toLine])).toEqual([
      [' ', 10, 10], ['-', 11, null], ['+', null, 11],
    ]);
  });

  it('shows markers and text for each line', () => {
    render(<MantineProvider><HunkView hunks={[hunk]} /></MantineProvider>);
    expect(screen.getByText('old')).toBeTruthy();
    expect(screen.getByText('new')).toBeTruthy();
    expect(screen.getByTestId('hunk-view').querySelectorAll('[data-op="+"]')).toHaveLength(1);
    expect(screen.getByTestId('hunk-view').querySelectorAll('[data-op="-"]')).toHaveLength(1);
  });
});
