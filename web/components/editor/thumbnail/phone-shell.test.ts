import { describe, expect, it } from 'vitest';
import { phoneStageHeight, PICTURE_DOCK, sheetHeightPx } from './phone-shell';

describe('picture phone shell geometry', () => {
  it('pins the stage at 45 dvh with no sheet', () => {
    expect(phoneStageHeight(844, 200, null)).toBe(380);
  });
  it('shrinks the stage to end above an open sheet, never below the floor', () => {
    expect(phoneStageHeight(844, 200, 'peek')).toBe(380);
    expect(phoneStageHeight(844, 200, 'half')).toBe(256);
    expect(phoneStageHeight(844, 200, 'full')).toBe(160);
  });
  it('sheet heights match the detents', () => {
    expect(sheetHeightPx('peek', 800)).toBe(120);
    expect(sheetHeightPx('half', 800)).toBe(360);
    expect(sheetHeightPx('full', 800)).toBe(720);
  });
  it('docks Layers, Text, AI and Canvas', () => {
    expect(PICTURE_DOCK.map((d) => d.label)).toEqual(['Layers', 'Text', 'AI', 'Canvas']);
  });
});
