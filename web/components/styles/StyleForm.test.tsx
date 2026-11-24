// @vitest-environment jsdom
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { StyleForm } from './StyleForm';
import { RUN_FORMAT_OPTIONS, STYLE_AXES, choiceFor, describeStyle, isEmptyStyle, suggestedFormatFor } from './style-options';
import { renderHook } from '@testing-library/react';
import { useT } from '@/i18n';
import type { StylePreset } from '@/lib/types/styles';

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
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: { addEventListener: vi.fn(), removeEventListener: vi.fn(), ready: Promise.resolve() },
  });
});
afterEach(() => cleanup());

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('style options', () => {
  it('covers every axis the server accepts, each with its full set of choices', () => {
    expect(STYLE_AXES.map((a) => a.key)).toEqual(['pacing', 'storytelling', 'editing', 'tone', 'captions', 'color', 'music', 'graphics', 'dynamics']);
    expect(STYLE_AXES.find((a) => a.key === 'pacing')!.options.map((o) => o.value)).toEqual(['Slow', 'Medium', 'Fast', 'BeatSynced']);
    expect(STYLE_AXES.find((a) => a.key === 'color')!.options).toHaveLength(7);
  });

  it('offers every run format, Original first', () => {
    expect(RUN_FORMAT_OPTIONS.map((o) => o.value)).toEqual(['Original', 'Vertical', 'Square', 'Portrait', 'Landscape']);
  });

  it('summarizes a style in short words', () => {
    expect(describeStyle({ pacing: 'Fast', storytelling: 'HookFirst', notes: 'x' }, renderHook(() => useT()).result.current)).toBe('Fast · Hook first · notes');
    expect(describeStyle({ pacing: 'Fast', dynamics: 'Fast' }, renderHook(() => useT()).result.current)).toBe('Fast · Fast effects');
    expect(describeStyle(null, renderHook(() => useT()).result.current)).toBe('');
  });

  it('offers the dynamics axis with its three values and treats it as a choice', () => {
    const axis = STYLE_AXES.find((a) => a.key === 'dynamics');
    expect(axis?.options.map((o) => o.value)).toEqual(['Calm', 'Medium', 'Fast']);
    expect(isEmptyStyle({ dynamics: 'Medium' })).toBe(false);
  });

  it('knows an empty style', () => {
    expect(isEmptyStyle({})).toBe(true);
    expect(isEmptyStyle({ notes: '  ' })).toBe(true);
    expect(isEmptyStyle({ tone: 'Calm' })).toBe(false);
  });

  it('decodes a picker value into the run request', () => {
    expect(choiceFor('none')).toEqual({});
    expect(choiceFor('preset:cinematic')).toEqual({ stylePresetKey: 'cinematic' });
    expect(choiceFor('style:abc')).toEqual({ styleId: 'abc' });
    const presets = [{ key: 'cinematic', suggestedFormat: 'Landscape' }] as StylePreset[];
    expect(suggestedFormatFor('preset:cinematic', presets)).toBe('Landscape');
    expect(suggestedFormatFor('style:abc', presets)).toBeNull();
  });
});

describe('StyleForm', () => {
  it('will not save a style without a name or any choice', () => {
    const onSubmit = vi.fn();
    wrap(<StyleForm onSubmit={onSubmit} onCancel={vi.fn()} />);
    const save = screen.getByRole('button', { name: 'Save style' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /Name/ }), { target: { value: 'Reel' } });
    expect(save.disabled).toBe(true);
    fireEvent.change(screen.getByRole('textbox', { name: /Notes for the AI/ }), { target: { value: '  bright and bold ' } });
    fireEvent.click(save);
    expect(onSubmit).toHaveBeenCalledWith({ name: 'Reel', description: null, projectId: null, config: { notes: 'bright and bold' } });
  });

  it('starts from a preset and pins to the project only when asked', () => {
    const onSubmit = vi.fn();
    wrap(<StyleForm projectId="p1" initial={{ name: 'Copy', config: { pacing: 'Slow' } }} onSubmit={onSubmit} onCancel={vi.fn()} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Only use in this project' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save style' }));
    expect(onSubmit).toHaveBeenCalledWith({ name: 'Copy', description: null, projectId: 'p1', config: { pacing: 'Slow', notes: null } });
  });
});
