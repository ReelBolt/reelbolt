import { describe, expect, it } from 'vitest';
import { isHelpKey, shortcutBlocked } from '../keymap';
import { deviceClassOf } from '../device-class';

const k = (key: string, mods: Partial<Record<'ctrlKey' | 'metaKey' | 'altKey', boolean>> = {}) => ({ key, ctrlKey: false, metaKey: false, altKey: false, ...mods });

describe('isHelpKey', () => {
  it('accepts a bare ?', () => expect(isHelpKey(k('?'))).toBe(true));
  it('rejects other keys and modified ?', () => {
    expect(isHelpKey(k('/'))).toBe(false);
    expect(isHelpKey(k('?', { ctrlKey: true }))).toBe(false);
    expect(isHelpKey(k('?', { metaKey: true }))).toBe(false);
    expect(isHelpKey(k('?', { altKey: true }))).toBe(false);
  });
  it('is blocked inside a text field', () => {
    const input = document.createElement('input');
    expect(shortcutBlocked(input, 'shift+/')).toBe(true);
  });
});

describe('tablet breakpoints', () => {
  const t = (width: number, coarsePointer = true) => deviceClassOf({ width, height: 900, coarsePointer });
  it('767/768/1100/1101 on a touch screen', () => {
    expect(t(767)).toBe('tablet');
    expect(t(768)).toBe('tablet');
    expect(t(1100)).toBe('tablet');
    expect(t(1101)).toBe('tablet');
    expect(t(1101, false)).toBe('desktop');
    expect(t(1100, false)).toBe('tablet');
  });
});
