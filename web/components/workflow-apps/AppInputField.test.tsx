// @vitest-environment jsdom
/** A `choice` input as a segmented control: long labels stack on a phone, short ones and wide screens stay in a row. */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { AppInputField, labelsFitOneRow } from './AppInputField';
import type { AppManifestInput } from '@/lib/types/workflow';

beforeAll(() => {
  // Mantine's SegmentedControl observes its size; jsdom has neither.
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  global.matchMedia = ((q: string) => ({
    matches: false, media: q, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
  })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

const LONG = [
  { value: '1K', label: '1K (~1024 px) 8,8 cr' },
  { value: '2K', label: '2K (~2048 px) 12,6 cr' },
  { value: '4K', label: '4K (~4096 px) 17 cr' },
];
const SHORT = [
  { value: 30, label: '30 s' },
  { value: 60, label: '60 s' },
  { value: 90, label: '90 s' },
];

function field(options: Array<{ value: string | number; label: string }>, phone: boolean, onChange = vi.fn()) {
  const input = { kind: 'input', id: 'size', type: 'choice', label: 'Resolution', default: options[0]!.value, options } as AppManifestInput & { id: string };
  render(
    <MantineProvider>
      <AppInputField input={input} value={options[0]!.value} onChange={onChange} projectId="p1" workflowId="w1" phone={phone} />
    </MantineProvider>,
  );
  return onChange;
}

describe('labelsFitOneRow', () => {
  it('accepts short labels and rejects long ones', () => {
    expect(labelsFitOneRow(['30 s', '60 s', '90 s'])).toBe(true);
    expect(labelsFitOneRow(LONG.map((o) => o.label))).toBe(false);
    expect(labelsFitOneRow(['Vertical', 'Square', 'Wide', 'Tall'])).toBe(true);
    expect(labelsFitOneRow(['aaaaaaaaaaa'])).toBe(false);
    expect(labelsFitOneRow(['aaaaaaaaaa', 'aaaaaaaaaa', 'aaaaaaaaaa'])).toBe(false);
  });
});

describe('AppInputField choice segments', () => {
  it('stacks long labels on a phone and every option selects its typed value', () => {
    const onChange = field(LONG, true);
    expect(screen.getByRole('radiogroup').getAttribute('data-orientation')).toBe('vertical');
    expect(screen.getAllByRole('radio')).toHaveLength(3);
    fireEvent.click(screen.getByRole('radio', { name: /4K/ }));
    expect(onChange).toHaveBeenCalledWith('4K');
    fireEvent.click(screen.getByRole('radio', { name: /2K/ }));
    expect(onChange).toHaveBeenCalledWith('2K');
  });

  it('keeps short labels in a row on a phone, and sends numbers as numbers', () => {
    const onChange = field(SHORT, true);
    expect(screen.getByRole('radiogroup').getAttribute('data-orientation')).toBe('horizontal');
    fireEvent.click(screen.getByRole('radio', { name: '90 s' }));
    expect(onChange).toHaveBeenCalledWith(90);
  });

  it('keeps long labels in a row off the phone', () => {
    field(LONG, false);
    expect(screen.getByRole('radiogroup').getAttribute('data-orientation')).toBe('horizontal');
  });
});
