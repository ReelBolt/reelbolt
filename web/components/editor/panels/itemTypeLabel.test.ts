import { describe, expect, it } from 'vitest';
import { itemTypeLabel } from './itemTypeLabel';

describe('itemTypeLabel', () => {
  it('never shows an internal type name', () => {
    expect(itemTypeLabel('mediaClip', 'video')).toBe('Video clip');
    expect(itemTypeLabel('mediaClip', 'audio')).toBe('Audio clip');
    expect(itemTypeLabel('titleClip', 'video')).toBe('Title');
    expect(itemTypeLabel('insertClip', 'video')).toBe('Picture');
  });
});
