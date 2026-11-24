// @vitest-environment jsdom
/**
 * Customer e2e findings 24/29 and item 8: the compile step leads with plain "Format and length"
 * controls (shape, fit, target length), keeps codec/preset/CRF behind "Advanced", and warns when
 * music or sound effects are on but the project has no audio files.
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

describe('VideoCompileStepConfig — format and length', () => {
  it('offers the five plain output formats', () => {
    renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /^Shape/ }));
    for (const name of ['Original size', 'Vertical 9:16 — Reels/TikTok/Shorts', 'Square 1:1 — feed', 'Landscape 16:9 — YouTube', 'Portrait 4:5 — feed']) {
      expect(screen.getByRole('option', { name })).toBeTruthy();
    }
  });

  it('routes a chosen format through onChange', () => {
    const onChange = renderConfig();
    fireEvent.click(screen.getByRole('combobox', { name: /^Shape/ }));
    fireEvent.click(screen.getByRole('option', { name: 'Vertical 9:16 — Reels/TikTok/Shorts' }));
    expect(onChange.mock.calls.at(-1)![0].outputFormat).toBe('Vertical');
  });

  it('shows the fit choice only for a non-original format', () => {
    renderConfig();
    expect(screen.queryByRole('combobox', { name: 'When a clip has a different shape' })).toBeNull();
    cleanup();
    renderConfig({ outputFormat: 'Square' });
    const fit = screen.getByRole('combobox', { name: 'When a clip has a different shape' }) as HTMLInputElement;
    expect(fit.value).toBe('Fill the frame (crop the middle)');
    fireEvent.click(fit);
    expect(screen.getByRole('option', { name: 'Fit whole clip (bars)' })).toBeTruthy();
    expect(screen.getByRole('option', { name: 'Fill the frame, keep the subject in shot' })).toBeTruthy();
  });

  it('explains where a subject crop gets its subject from', () => {
    renderConfig({ outputFormat: 'Vertical', outputFit: 'Subject' });
    expect(screen.getByText(/Find the subject of each shot/)).toBeTruthy();
  });

  it('routes a target length in whole seconds', () => {
    const onChange = renderConfig();
    fireEvent.change(screen.getByRole('textbox', { name: /^Target length \(seconds\)/ }), { target: { value: '15' } });
    expect(onChange.mock.calls.at(-1)![0].targetDurationSec).toBe(15);
  });

  it('keeps codec, preset and CRF behind the Advanced toggle', async () => {
    renderConfig();
    expect(screen.queryByRole('combobox', { name: /Video codec/ })).toBeNull();
    expect(screen.queryByRole('textbox', { name: /CRF/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Advanced' }));
    expect(await screen.findByRole('textbox', { name: /Quality \(CRF\)/ })).toBeTruthy();
    expect(document.body.textContent).not.toMatch(/opaque ids/);
  });
});

describe('VideoCompileStepConfig — music and sound effects without audio files', () => {
  it('tells the customer to upload audio when music is on and the project has none', () => {
    FILES.current = [{ id: 'v', mimeType: 'video/mp4', originalFileName: 'clip.mp4' }];
    renderConfig({ enableMusic: true });
    expect(screen.getByText(/Your project has no music files/)).toBeTruthy();
    expect(screen.getByText(/no built-in library yet/)).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Files' }).getAttribute('href')).toBe('/projects/p1?tab=files');
  });

  it('says the same for sound effects', () => {
    FILES.current = [];
    renderConfig({ enableSfx: true });
    expect(screen.getByText(/Your project has no sound-effect files/)).toBeTruthy();
  });

  it('stays quiet when the project has an audio file, or when music is off', () => {
    FILES.current = [{ id: 'a', mimeType: 'audio/mpeg', originalFileName: 'bed.mp3' }];
    renderConfig({ enableMusic: true });
    expect(screen.queryByText(/Your project has no/)).toBeNull();
    cleanup();
    FILES.current = [];
    renderConfig({ enableMusic: false });
    expect(screen.queryByText(/Your project has no/)).toBeNull();
  });
});
