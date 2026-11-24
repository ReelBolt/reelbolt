// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { STAGE_MIN_HEIGHT, spaceBelow, stageHeight } from './stage-fit';

afterEach(() => {
  document.body.innerHTML = '';
  document.body.removeAttribute('style');
});

describe('stageHeight (d6 N1)', () => {
  it('at 1440 × 900 the stage ends inside the window: the audit measured 222 px above it and 37 px below', () => {
    const h = stageHeight(900, 222, 37);
    expect(222 + h + 37).toBeLessThanOrEqual(900);
    expect(h).toBe(641);
  });

  it('never goes below the smallest usable stage on a short window', () => {
    expect(stageHeight(500, 222, 37)).toBe(STAGE_MIN_HEIGHT);
  });

  it('rounds down, so a fraction of a pixel never adds a scrollbar', () => {
    expect(stageHeight(900.6, 222.3, 37)).toBe(641);
  });
});

describe('spaceBelow', () => {
  it('adds up the bottom padding, border and margin of the element and every box around it', () => {
    document.body.style.margin = '0';
    document.body.style.paddingBottom = '16px';
    const container = document.createElement('div');
    container.style.paddingBottom = '20px';
    container.style.borderBottom = '1px solid red';
    const layout = document.createElement('div');
    layout.style.marginBottom = '0px';
    container.appendChild(layout);
    document.body.appendChild(container);
    expect(spaceBelow(layout)).toBe(37);
  });
});

describe('PHONE_STAGE_MIN_HEIGHT', () => {
  it('matches the phone stage min-height in the stylesheet', async () => {
    const { PHONE_STAGE_MIN_HEIGHT } = await import('./stage-fit');
    expect(PHONE_STAGE_MIN_HEIGHT).toBe(300);
  });
});
