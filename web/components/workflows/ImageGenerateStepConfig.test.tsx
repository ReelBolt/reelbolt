// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';
import type { Mock } from 'vitest';
import { ImageGenerateStepConfig, createDefaultImageGenerateStepConfig } from './ImageGenerateStepConfig';
import type { ImageGenerateStepConfig as Cfg } from '@/lib/types/workflow';

const seen = vi.hoisted(() => ({ capabilities: [] as (string | null | undefined)[] }));
vi.mock('@/lib/hooks/use-auth', () => ({
  useAuth: () => ({ isPlatformAdmin: true, isOrgAdmin: true, user: { orgRole: 'Owner' } }),
}));
vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [{ id: 'img1', originalFileName: 'logo.png', mimeType: 'image/png' }, { id: 'vid1', originalFileName: 'clip.mp4', mimeType: 'video/mp4' }] }),
}));
vi.mock('@/lib/hooks/use-media', () => ({ useMediaPool: () => ({ data: [] }) }));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: (capability?: string | null) => {
    seen.capabilities.push(capability);
    return { data: [], isLoading: false, error: undefined };
  },
}));

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
afterEach(() => { cleanup(); seen.capabilities = []; });

function renderConfig(overrides: Partial<Cfg> = {}, onChange: Mock = vi.fn()) {
  render(
    <MantineProvider>
      <ImageGenerateStepConfig config={{ ...createDefaultImageGenerateStepConfig(), ...overrides }} onChange={onChange} currentStepIndex={0} projectId="p1" />
    </MantineProvider>,
  );
  return onChange;
}
const last = (m: Mock) => m.mock.calls.at(-1)![0] as Cfg;

describe('createDefaultImageGenerateStepConfig', () => {
  it('mirrors the C# record: Generate, one picture, saved to files, no consent and nothing optional set', () => {
    expect(createDefaultImageGenerateStepConfig()).toEqual({
      mode: 'Generate', prompt: '', instruction: null, source: null, variations: 1, maxSpendCredits: null,
      providerId: null, itemList: null, aspectRatio: null, width: null, height: null, resolution: null, outputWidth: null, outputHeight: null, registerProjectFile: true,
      allowSourceMediaEgress: false, fileNamePrefix: null,
    });
  });
});

describe('ImageGenerateStepConfig', () => {
  it('Generate shows the prompt and variations, not the source or consent, and reads the ImageGeneration providers', () => {
    renderConfig();
    expect(screen.getByLabelText('Description')).toBeTruthy();
    expect(screen.getByLabelText('Pictures')).toBeTruthy();
    expect(screen.queryByText('Allow sending this picture to the AI service')).toBeNull();
    expect(seen.capabilities).toContain('ImageGeneration');
  });

  it('warns when no spending limit is set', () => {
    renderConfig();
    expect(screen.getByText(/No spending limit is set/)).toBeTruthy();
  });

  it('switching to Edit clears the Generate-only fields, seeds a source, keeps consent off', () => {
    const onChange = renderConfig({ prompt: 'a lighthouse', variations: 3, width: 512, height: 512, providerId: 'x' });
    fireEvent.click(screen.getByText('Edit a picture'));
    expect(last(onChange)).toMatchObject({
      mode: 'Edit', prompt: null, instruction: '', variations: 3, width: null, height: null, providerId: null,
      source: { kind: 'ProjectFile', projectFileId: null }, allowSourceMediaEgress: false,
    });
  });

  it('Edit shows the instruction, source picker and egress consent, shows variations, and reads the ImageEdit providers', () => {
    renderConfig({ mode: 'Edit', prompt: null, instruction: '', source: { kind: 'ProjectFile', projectFileId: null } });
    expect(screen.getByLabelText('What to change')).toBeTruthy();
    expect(screen.getByText('Picture to edit')).toBeTruthy();
    expect(screen.getByLabelText('Pictures')).toBeTruthy();
    expect(seen.capabilities).toContain('ImageEdit');
  });

  it('ticking the egress consent patches allowSourceMediaEgress', () => {
    const onChange = renderConfig({ mode: 'Edit', prompt: null, instruction: 'x', source: { kind: 'ProjectFile', projectFileId: 'img1' } });
    fireEvent.click(screen.getByLabelText('Allow sending this picture to the AI service'));
    expect(last(onChange).allowSourceMediaEgress).toBe(true);
  });

  it('patches the prompt', () => {
    const onChange = renderConfig();
    fireEvent.change(screen.getByLabelText('Description'), { target: { value: 'a fox' } });
    expect(last(onChange).prompt).toBe('a fox');
  });

  it('setting an exact size clears the aspect ratio and Generate width and height, and hides them', () => {
    const onChange = renderConfig({ aspectRatio: '16:9', width: null, height: null });
    expect((screen.getByLabelText('Exact width (pixels)') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('Aspect ratio') as HTMLInputElement).disabled).toBe(false);
    cleanup();
    const o2 = renderConfig({ outputWidth: 2000, outputHeight: 2000 });
    expect((screen.getByLabelText('Aspect ratio') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByLabelText('Width (pixels)')).toBeNull();
    fireEvent.change(screen.getByLabelText('Exact width (pixels)'), { target: { value: '1500' } });
    expect(last(o2)).toMatchObject({ outputWidth: 1500, outputHeight: 2000, aspectRatio: null, width: null, height: null });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('typing an exact size patches outputWidth and clears the other shape fields', () => {
    const onChange = renderConfig();
    fireEvent.change(screen.getByLabelText('Exact width (pixels)'), { target: { value: '2000' } });
    expect(last(onChange)).toMatchObject({ outputWidth: 2000, aspectRatio: null, width: null, height: null });
    expect(screen.getByText(/2000 x 2000/)).toBeTruthy();
  });

  it('shows the covering-tier hint when the chosen resolution is too small for the exact size', () => {
    renderConfig({ resolution: '1K', outputWidth: 2000, outputHeight: 2000 });
    expect(screen.getByText(/Too small: 1K/)).toBeTruthy();
  });

  it('works in Edit mode too', () => {
    renderConfig({ mode: 'Edit', prompt: null, instruction: '', source: { kind: 'ProjectFile', projectFileId: null }, resolution: '2K' });
    expect(screen.getByLabelText('Exact height (pixels)')).toBeTruthy();
  });
});
