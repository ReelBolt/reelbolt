// @vitest-environment jsdom
/**
 * The music-driven reel findings (customer end-to-end findings, October 2026): story order, cutting to
 * the beat, several insert contents and insert-content dips are all reachable from the compile form.
 */
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { VideoCompileStepConfig, createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import type { VideoCompileStepConfig as Cfg } from '@/lib/types/workflow';

const { FILES } = vi.hoisted(() => ({ FILES: { current: [] as { id: string; mimeType: string; originalFileName: string }[] } }));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: FILES.current, error: null, isLoading: false }) }));

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => cleanup());

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <VideoCompileStepConfig config={{ ...createDefaultVideoCompileStepConfig(), ...overrides }} onChange={onChange} projectId="p1" />
    </MantineProvider>,
  );
  return onChange;
}

describe('VideoCompileStepConfig — story order', () => {
  it('defaults to filmed order and routes the editor order through onChange', () => {
    const onChange = renderConfig();
    const order = screen.getByRole('combobox', { name: /^Order of the clips/ }) as HTMLInputElement;
    expect(order.value).toBe('In the order it was filmed (default)');
    fireEvent.click(order);
    fireEvent.click(screen.getByRole('option', { name: "In the editor's order" }));
    expect(onChange.mock.calls.at(-1)![0].keepOrder).toBe('AsListed');
  });

  it('hides the order when every source is kept whole', () => {
    renderConfig({ keepWholeSources: true });
    expect(screen.queryByRole('combobox', { name: /^Order of the clips/ })).toBeNull();
  });
});

describe('VideoCompileStepConfig — cutting to the beat', () => {
  it('offers beat sync only with music on, defaulting to off', () => {
    renderConfig();
    expect(screen.queryByRole('combobox', { name: /^Cut to the beat/ })).toBeNull();
    cleanup();
    const onChange = renderConfig({ enableMusic: true });
    const sync = screen.getByRole('combobox', { name: /^Cut to the beat/ }) as HTMLInputElement;
    expect(sync.value).toBe('Off (default)');
    fireEvent.click(sync);
    fireEvent.click(screen.getByRole('option', { name: 'On every bar (four beats)' }));
    expect(onChange.mock.calls.at(-1)![0].beatSync).toBe('Bar');
  });
});

describe('VideoCompileStepConfig — several insert contents and dips', () => {
  it('adds a second content and routes its clip number as a 0-based source index', () => {
    FILES.current = [
      { id: 'f1', mimeType: 'video/mp4', originalFileName: 'phone-ui.mp4' },
      { id: 'f2', mimeType: 'video/mp4', originalFileName: 'laptop-ui.mp4' },
    ];
    const onChange = renderConfig({ enableInserts: true, insertContentProjectFileId: 'f1' });
    fireEvent.click(screen.getByRole('button', { name: 'Add content for other screens' }));
    expect(onChange.mock.calls.at(-1)![0].insertContents).toEqual([{ projectFileId: 'f1' }]);

    cleanup();
    const onChange2 = renderConfig({ enableInserts: true, insertContents: [{ projectFileId: 'f2' }] });
    fireEvent.change(screen.getByRole('textbox', { name: /^Only on screens in clip number/ }), { target: { value: '2' } });
    expect(onChange2.mock.calls.at(-1)![0].insertContents).toEqual([{ projectFileId: 'f2', sourceIndex: 1 }]);
  });

  it('removes a content row', () => {
    FILES.current = [{ id: 'f2', mimeType: 'video/mp4', originalFileName: 'laptop-ui.mp4' }];
    const onChange = renderConfig({ enableInserts: true, insertContents: [{ projectFileId: 'f2' }] });
    fireEvent.click(screen.getByRole('button', { name: 'Remove this content' }));
    expect(onChange.mock.calls.at(-1)![0].insertContents).toEqual([]);
  });

  it('defaults dips to freezing the last picture and offers cutting them', () => {
    const onChange = renderConfig({ enableInserts: true });
    const dips = screen.getByRole('combobox', { name: /^When your video fades to black/ }) as HTMLInputElement;
    expect(dips.value).toBe('Keep showing the last picture (default)');
    fireEvent.click(dips);
    fireEvent.click(screen.getByRole('option', { name: 'Skip the black part' }));
    expect(onChange.mock.calls.at(-1)![0].insertContentDips).toBe('Cut');
  });
});
