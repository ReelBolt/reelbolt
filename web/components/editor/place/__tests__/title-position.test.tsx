import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TitlePositionPad } from '../TitlePositionPad';
import { titleTemplateLayout } from '@/lib/editing/preview/title-template';
import { buildGraphicOps } from '@/lib/editing/structure/graphics';
import { doc, media, track, TRACKS } from '@/lib/editing/__tests__/fixtures';
import type { TitleClip } from '@/lib/editing/types';

afterEach(cleanup);
Object.defineProperty(window, 'matchMedia', { writable: true, value: (query: string) => ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) });

const wrap = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

describe('TitlePositionPad', () => {
  it('names all nine spots plainly and chooses one on click', () => {
    const onChange = vi.fn();
    wrap(<TitlePositionPad value={null} onChange={onChange} />);
    for (const name of ['Top left', 'Top centre', 'Top right', 'Middle left', 'Centre', 'Middle right', 'Bottom left', 'Bottom centre', 'Bottom right']) expect(screen.getByRole('radio', { name })).toBeTruthy();
    fireEvent.click(screen.getByRole('radio', { name: 'Bottom right' }));
    expect(onChange).toHaveBeenCalledWith('bottomRight');
  });

  it('arrow keys move the choice, and Template default clears it', () => {
    const onChange = vi.fn();
    wrap(<TitlePositionPad value="center" onChange={onChange} />);
    fireEvent.keyDown(screen.getByRole('radio', { name: 'Centre' }), { key: 'ArrowRight' });
    expect(onChange).toHaveBeenLastCalledWith('middleRight');
    fireEvent.click(screen.getByText('Template default'));
    expect(onChange).toHaveBeenLastCalledWith(null);
  });

  it('every target is 44 px', () => {
    wrap(<TitlePositionPad value={null} onChange={() => {}} />);
    expect((screen.getByRole('radio', { name: 'Top left' }) as HTMLElement).style.width).toBe('44px');
    expect((screen.getByRole('radio', { name: 'Top left' }) as HTMLElement).style.height).toBe('44px');
  });
});

describe('title position in the preview and builder', () => {
  it('the preview anchors a title like the render (captions pad: x = w/12, y = h/12 top, bottom = h - text_h - h/12)', () => {
    const top = titleTemplateLayout(null, 1920, 1080, 60, 'topLeft');
    expect(top).toMatchObject({ align: 'left', x: 160, y: 90 });
    const bottomRight = titleTemplateLayout('boxed', 1920, 1080, 50, 'bottomRight');
    expect(bottomRight).toMatchObject({ align: 'right', x: 1760, y: 1080 - 60 - 90 });
    expect(bottomRight.box).not.toBeNull();
  });

  it('lower third and card keep their own place, as the render does', () => {
    expect(titleTemplateLayout('lowerThird', 1920, 1080, 50, 'topRight')).toEqual(titleTemplateLayout('lowerThird', 1920, 1080, 50));
    expect(titleTemplateLayout('card', 1920, 1080, 50, 'topRight')).toEqual(titleTemplateLayout('card', 1920, 1080, 50));
  });

  it('the builder stores the chosen position and leaves it absent for the template default', () => {
    let n = 0;
    const newId = () => `g${++n}`;
    const d = doc([track(TRACKS.v1, 'video', [media('a', 0, 100)]), track(TRACKS.v2, 'video', [])]);
    const withPos = buildGraphicOps(d, { spec: { kind: 'title', text: 'Hi', template: 'plain', position: 'topCenter' }, at: 0, newId });
    const op = withPos.ops[0] as { item?: TitleClip; track?: { items: TitleClip[] } };
    expect((op.item ?? op.track?.items[0])?.position).toBe('topCenter');
    const without = buildGraphicOps(d, { spec: { kind: 'title', text: 'Hi', template: 'plain' }, at: 0, newId });
    const op2 = without.ops[0] as { item?: TitleClip; track?: { items: TitleClip[] } };
    expect((op2.item ?? op2.track?.items[0])).not.toHaveProperty('position');
  });
});
