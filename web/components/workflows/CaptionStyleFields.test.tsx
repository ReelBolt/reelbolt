// @vitest-environment jsdom
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { useState } from 'react';
import { MantineProvider } from '@mantine/core';
import {
  CaptionStyleFields,
  CAPTION_FONT_OPTIONS,
  isAutoCaptionSize,
  captionColorCss,
  captionFontFamily,
} from './CaptionStyleFields';
import { VideoCompileStepConfig, createDefaultVideoCompileStepConfig } from './VideoCompileStepConfig';
import type { VideoCompileStepConfig as VideoCompileStepConfigValue } from '@/lib/types/workflow';
import { STEP_CONFIG_SCHEMAS } from '@/lib/schemas/workflow-step-configs';

vi.mock('@/lib/hooks/use-files', () => ({
  useProjectFiles: () => ({ data: [], error: null, isLoading: false }),
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

// Vitest runs without `globals`, so Testing Library's auto-cleanup never registers.
afterEach(() => cleanup());

function Harness({ initial, onChange }: { initial: VideoCompileStepConfigValue; onChange?: (c: VideoCompileStepConfigValue) => void }) {
  const [config, setConfig] = useState(initial);
  return (
    <MantineProvider>
      <CaptionStyleFields
        config={config}
        patch={(p) => {
          const next = { ...config, ...p };
          setConfig(next);
          onChange?.(next);
        }}
      />
    </MantineProvider>
  );
}

const base = (): VideoCompileStepConfigValue => ({
  ...createDefaultVideoCompileStepConfig(),
  enableVoiceover: true,
  voiceoverStepOrder: 2,
  enableCaptions: true,
});

describe('CaptionStyleFields', () => {
  it('offers exactly the fonts the engine ships, each with a live sample in its own font', () => {
    render(<Harness initial={base()} />);

    const schemaFonts = (STEP_CONFIG_SCHEMAS.VideoCompile.properties as Record<string, { enum: unknown[] }>).captionFont.enum;
    expect(CAPTION_FONT_OPTIONS.map((o) => o.value)).toEqual(schemaFonts.filter((v) => v !== null));

    const radios = screen.getAllByRole('radio');
    expect(radios).toHaveLength(CAPTION_FONT_OPTIONS.length);
    expect(screen.getByTestId('caption-font-sample-Anton').style.fontFamily).toContain('ReelBolt Caption Anton');
    expect(screen.getByTestId('caption-font-sample-BebasNeue').style.fontFamily).toContain('ReelBolt Caption Bebas Neue');
    // Automatic samples the style's own font (Inter for the default Subtitle style).
    expect(screen.getByTestId('caption-font-sample-Auto').style.fontFamily).toContain('ReelBolt Caption Inter');
    expect(screen.getByRole('radio', { name: /^Automatic/ }).getAttribute('aria-checked')).toBe('true');
  });

  it('picking a font stores its enum name and marks it selected', () => {
    const onChange = vi.fn();
    render(<Harness initial={base()} onChange={onChange} />);

    fireEvent.click(screen.getByRole('radio', { name: /^Montserrat/ }));

    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ captionFont: 'Montserrat' }));
    expect(screen.getByRole('radio', { name: /^Montserrat/ }).getAttribute('aria-checked')).toBe('true');
    expect(screen.getByTestId('caption-preview').querySelector('div')!.style.fontFamily).toContain('Montserrat');
  });

  it('treats the old default size of 4 as automatic, and turning automatic off reveals a manual size', () => {
    const onChange = vi.fn();
    render(<Harness initial={{ ...base(), captionFontSizePct: 4 }} onChange={onChange} />);

    const auto = screen.getByRole('switch', { name: /Automatic text size/ }) as HTMLInputElement;
    expect(auto.checked).toBe(true);
    expect(screen.queryByLabelText(/Text size \(% of the video/)).toBeNull();

    fireEvent.click(auto);
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ captionFontSizePct: 6 }));
    expect(screen.getByLabelText(/Text size \(% of the video/)).toBeTruthy();

    fireEvent.click(screen.getByRole('switch', { name: /Automatic text size/ }));
    expect(onChange).toHaveBeenLastCalledWith(expect.objectContaining({ captionFontSizePct: 0 }));
  });

  it('Punch previews one word in the middle with no box; a vertical format previews a portrait frame', () => {
    render(<Harness initial={{ ...base(), captionStyle: 'Punch', outputFormat: 'Vertical' }} />);

    const preview = screen.getByTestId('caption-preview');
    expect(preview.getAttribute('data-position')).toBe('Center');
    expect(preview.textContent).toBe('Ship');
    expect(preview.style.height).toBe('240px');
    expect((preview.querySelector('span') as HTMLElement).style.background).toBe('');
  });

  it('Karaoke shows the spoken-word colour picker; Subtitle does not', () => {
    render(<Harness initial={{ ...base(), captionStyle: 'Karaoke' }} />);
    expect(screen.getByText('Spoken-word colour')).toBeTruthy();
    cleanup();
    render(<Harness initial={{ ...base(), captionStyle: 'Subtitle' }} />);
    expect(screen.queryByText('Spoken-word colour')).toBeNull();
  });

  it('is what the compile editor renders once captions are switched on', () => {
    render(
      <MantineProvider>
        <VideoCompileStepConfig config={base()} onChange={() => {}} allSteps={[]} currentStepIndex={2} />
      </MantineProvider>,
    );
    expect(screen.getByRole('radiogroup')).toBeTruthy();
    expect(screen.getByTestId('caption-preview')).toBeTruthy();
  });
});

describe('caption helpers', () => {
  it('isAutoCaptionSize mirrors CaptionLayout.IsAutoSize', () => {
    expect([undefined, null, 0, 4].map((v) => isAutoCaptionSize(v))).toEqual([true, true, true, true]);
    expect([2, 3, 5, 12].map((v) => isAutoCaptionSize(v))).toEqual([false, false, false, false]);
  });

  it('captionColorCss accepts only the allowlisted vocabulary', () => {
    expect(captionColorCss('black@0.6')).toBe('rgba(0, 0, 0, 0.6)');
    expect(captionColorCss('#FF8000')).toBe('rgba(255, 128, 0, 1)');
    expect(captionColorCss('none')).toBeNull();
    expect(captionColorCss('red; background: url(x)')).toBeNull();
  });

  it('captionFontFamily follows the style when the font is Automatic', () => {
    expect(captionFontFamily('Auto', 'Punch')).toContain('Anton');
    expect(captionFontFamily('Auto', 'Karaoke')).toContain('Montserrat');
    expect(captionFontFamily('Poppins', 'Punch')).toContain('Poppins');
  });
});
