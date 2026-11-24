import { beforeAll, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { RunStepList, stepStateFor, type RunStepListItem } from './RunStepList';

const items: RunStepListItem[] = [
  { id: 'a', order: 1, name: 'Write the script', state: 'Completed', duration: '12s', resultId: 'ra' },
  { id: 'b', order: 2, name: 'Optional music', state: 'Skipped', duration: null, resultId: null },
  { id: 'c', order: 3, name: 'Design the look', state: 'Failed', duration: '3s', resultId: 'rc' },
];

describe('stepStateFor', () => {
  it('never says waiting on a finished run', () => {
    expect(stepStateFor(undefined, 'Passed')).toBe('Skipped');
    expect(stepStateFor(undefined, 'Failed')).toBe('NotRun');
    expect(stepStateFor(undefined, 'Stopped')).toBe('NotRun');
    expect(stepStateFor(undefined, 'Running')).toBe('Pending');
    expect(stepStateFor('Failed', 'Failed')).toBe('Failed');
  });
});

describe('RunStepList', () => {
  beforeAll(() => {
    window.matchMedia = ((q: string) => ({ matches: false, media: q, onchange: null, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false })) as unknown as typeof matchMedia;
  });

  it('shows status as text and opens a step on click', () => {
    const onSelect = vi.fn();
    render(<MantineProvider><RunStepList items={items} selectedResultId={null} onSelect={onSelect} /></MantineProvider>);
    expect(screen.getByText('Skipped')).toBeTruthy();
    expect(screen.queryByText('Waiting')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Step 3, Design the look, Failed/ }));
    expect(onSelect).toHaveBeenCalledWith('rc');
  });
});
