import { describe, expect, it } from 'vitest';
import { PHONE_MEDIA_QUERY, deviceClassOf, isPhoneViewport } from '../device-class';

describe('deviceClassOf (phone < 600 px wide, or < 500 px tall with a coarse pointer)', () => {
  it.each([
    [{ width: 360, height: 780, coarsePointer: true }, 'phone'],
    [{ width: 390, height: 844, coarsePointer: true }, 'phone'],
    [{ width: 599, height: 900, coarsePointer: false }, 'phone'],
    // Landscape phone: wide but short, and a finger.
    [{ width: 844, height: 390, coarsePointer: true }, 'phone'],
    // The same short window with a mouse is a small desktop window, not a phone.
    [{ width: 844, height: 390, coarsePointer: false }, 'tablet'],
    [{ width: 600, height: 900, coarsePointer: true }, 'tablet'],
    [{ width: 820, height: 1180, coarsePointer: true }, 'tablet'],
    [{ width: 1100, height: 800, coarsePointer: false }, 'tablet'],
    // A large touch screen keeps the coarse-pointer grammar.
    [{ width: 1366, height: 1024, coarsePointer: true }, 'tablet'],
    [{ width: 1280, height: 800, coarsePointer: false }, 'desktop'],
  ] as const)('%o is a %s', (facts, expected) => {
    expect(deviceClassOf(facts)).toBe(expected);
    expect(isPhoneViewport(facts)).toBe(expected === 'phone');
  });

  it('states the same phone rule as a media query', () => {
    expect(PHONE_MEDIA_QUERY).toBe('(max-width: 599.98px), (max-height: 499.98px) and (pointer: coarse)');
  });
});
