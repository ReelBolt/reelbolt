// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor, act } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { SmartBinEditor } from '../SmartBinEditor';
import { indexAnnotations } from '@/lib/editing/media/pool-browse';
import type { MediaPoolItem } from '@/lib/editing/media/types';

const item = (id: string, kind: MediaPoolItem['kind']): MediaPoolItem => ({
  mediaRef: `file:${id}`, kind, fileName: `${id}.mp4`, mimeType: 'video/mp4', sizeBytes: 1, category: 'userFiles', probe: null, bins: [], filmstripUrl: '',
});

class RO { observe() {} unobserve() {} disconnect() {} }

function setup(over: Partial<React.ComponentProps<typeof SmartBinEditor>> = {}) {
  global.matchMedia = ((query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => true, onchange: null })) as unknown as typeof matchMedia;
  (globalThis as unknown as { ResizeObserver: typeof RO }).ResizeObserver = RO;
  const onSave = vi.fn().mockResolvedValue(undefined);
  render(
    <MantineProvider>
      <SmartBinEditor
        opened mode="create" initialName="" initialRule={{}} onClose={() => {}} onSave={onSave}
        items={[item('a', 'video'), item('b', 'audio'), item('c', 'video')]}
        annotations={indexAnnotations([{ mediaRef: 'file:a', keywords: [], note: null, colorLabel: null, favourite: true, flag: false, updatedAt: '' }])}
        {...over}
      />
    </MantineProvider>,
  );
  return { onSave };
}

describe('SmartBinEditor (R05.6)', () => {
  afterEach(() => cleanup());

  it('refuses an empty rule with a message and saves nothing', async () => {
    const { onSave } = setup();
    fireEvent.change(await screen.findByTestId('smart-bin-name-input'), { target: { value: 'Everything' } });
    await act(async () => { fireEvent.click(screen.getByTestId('smart-bin-save')); });
    expect(screen.getByTestId('smart-bin-error').textContent).toMatch(/at least one/i);
    expect(onSave).not.toHaveBeenCalled();
  });

  it('shows how many sources match while editing, in plain words', async () => {
    setup();
    fireEvent.click(await screen.findByTestId('smart-bin-kind-video'));
    await waitFor(() => expect(screen.getByTestId('smart-bin-summary').getAttribute('data-matches')).toBe('2'));
    expect(screen.getByTestId('smart-bin-summary').textContent).toContain('Video: 2 sources match now');
  });

  it('saves the cleaned rule with the trimmed name', async () => {
    const { onSave } = setup();
    fireEvent.change(await screen.findByTestId('smart-bin-name-input'), { target: { value: '  Clips ' } });
    fireEvent.click(screen.getByTestId('smart-bin-kind-audio'));
    await act(async () => { fireEvent.click(screen.getByTestId('smart-bin-save')); });
    expect(onSave).toHaveBeenCalledWith('Clips', { kinds: ['audio'] });
  });

  it('edit mode starts from the saved rule', async () => {
    setup({ mode: 'edit', initialName: 'Music', initialRule: { kinds: ['audio'] } });
    expect((await screen.findByTestId('smart-bin-name-input') as HTMLInputElement).value).toBe('Music');
    expect(screen.getByTestId('smart-bin-summary').textContent).toContain('Audio: 1 source matches now');
  });
});
