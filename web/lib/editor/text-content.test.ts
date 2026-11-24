import { describe, expect, it } from 'vitest';
import {
  EDITOR_FONTS,
  TEXT_LIMITS,
  clampTextContent,
  defaultTextContent,
  findFont,
  googleFontsHref,
  nearestWeight,
  toWireContent,
  validateTextContent,
} from './text-content';
import { normalizeTextContent } from './model';

describe('validateTextContent', () => {
  it('accepts the contract example', () => {
    const content = defaultTextContent({
      text: 'COME PERMETTERSI\nUNA M2 A 23 ANNI',
      fontSize: 96,
      lineHeight: 1.05,
      maxWidth: 900,
    });
    expect(validateTextContent(content)).toEqual([]);
  });

  it('accepts the bounds exactly and refuses one step past them', () => {
    expect(validateTextContent(defaultTextContent({ fontSize: 8 }))).toEqual([]);
    expect(validateTextContent(defaultTextContent({ fontSize: 600 }))).toEqual([]);
    expect(validateTextContent(defaultTextContent({ fontSize: 7 }))).toHaveLength(1);
    expect(validateTextContent(defaultTextContent({ fontSize: 601 }))).toHaveLength(1);
    expect(validateTextContent(defaultTextContent({ stroke: { color: '#000', width: 64 } }))).toEqual([]);
    expect(validateTextContent(defaultTextContent({ stroke: { color: '#000', width: 65 } }))).toHaveLength(1);
    expect(validateTextContent(defaultTextContent({ stroke: { color: '#000', width: -1 } }))).toHaveLength(1);
  });

  it('caps the text at 2 000 characters', () => {
    expect(validateTextContent(defaultTextContent({ text: 'a'.repeat(TEXT_LIMITS.maxLength) }))).toEqual([]);
    const problems = validateTextContent(defaultTextContent({ text: 'a'.repeat(TEXT_LIMITS.maxLength + 1) }));
    expect(problems).toHaveLength(1);
    expect(problems[0]).toContain('2000');
  });

  it('refuses empty text, NaN numbers, a missing colour and an unknown alignment', () => {
    expect(validateTextContent(defaultTextContent({ text: '   ' }))).toContain('Type some text.');
    expect(validateTextContent(defaultTextContent({ fontSize: Number.NaN })).length).toBeGreaterThan(0);
    expect(validateTextContent(defaultTextContent({ color: '' }))).toContain('Choose a text colour.');
    expect(
      validateTextContent(defaultTextContent({ align: 'justify' as unknown as 'left' })),
    ).toContain('Alignment must be left, centre or right.');
  });

  it('reports every problem at once, in plain words with no field names', () => {
    const problems = validateTextContent(defaultTextContent({ text: '', fontSize: 1, color: '' }));
    expect(problems.length).toBe(3);
    for (const p of problems) expect(p).not.toMatch(/fontSize|maxWidth|stroke\.|undefined/);
  });

  it('checks shadow and wrap-width bounds only when present', () => {
    expect(validateTextContent(defaultTextContent({ shadow: null, maxWidth: null }))).toEqual([]);
    expect(
      validateTextContent(defaultTextContent({ shadow: { color: '#000', blur: 500, offsetX: 0, offsetY: 0 } })),
    ).toHaveLength(1);
    expect(validateTextContent(defaultTextContent({ maxWidth: 5 }))).toHaveLength(1);
  });
});

describe('clampTextContent', () => {
  it('pulls numbers into range so a slider can never produce a refused request', () => {
    const clamped = clampTextContent(
      defaultTextContent({ fontSize: 9000, stroke: { color: '#000', width: 900 }, text: 'a'.repeat(3000) }),
    );
    expect(clamped.fontSize).toBe(600);
    expect(clamped.stroke?.width).toBe(64);
    expect(clamped.text).toHaveLength(2000);
    expect(validateTextContent(clamped)).toEqual([]);
  });
});

describe('wire form', () => {
  it('omits an absent outline, shadow and wrap width rather than sending null', () => {
    const wire = toWireContent(defaultTextContent({ stroke: null, shadow: null, maxWidth: null }));
    expect(wire).not.toHaveProperty('stroke');
    expect(wire).not.toHaveProperty('shadow');
    expect(wire).not.toHaveProperty('maxWidth');
    expect(wire).toMatchObject({ text: 'Your title', fontFamily: 'Chakra Petch', uppercase: false });
  });

  it('omits a zero-width outline but keeps a real one', () => {
    expect(toWireContent(defaultTextContent({ stroke: { color: '#000', width: 0 } }))).not.toHaveProperty('stroke');
    expect(toWireContent(defaultTextContent({ stroke: { color: '#000', width: 6 } }))).toHaveProperty('stroke');
  });

  it('round-trips through normalizeTextContent back to the null-filled shape', () => {
    const original = defaultTextContent({ stroke: null, shadow: null, maxWidth: null });
    const back = normalizeTextContent(toWireContent(original) as unknown as typeof original);
    expect(back).toEqual(original);
  });
});

describe('the font list', () => {
  it('leads with the brand faces and every family ships at least one weight', () => {
    expect(EDITOR_FONTS[0].family).toBe('Chakra Petch');
    expect(EDITOR_FONTS.some((f) => f.family === 'JetBrains Mono')).toBe(true);
    for (const f of EDITOR_FONTS) expect(f.weights.length).toBeGreaterThan(0);
  });

  it('builds a Google Fonts CSS URL from the fixed list only', () => {
    const font = findFont('Chakra Petch')!;
    expect(googleFontsHref(font)).toBe(
      'https://fonts.googleapis.com/css2?family=Chakra+Petch:wght@400;500;600;700&display=swap',
    );
    expect(findFont('Comic Sans; drop table')).toBeNull();
  });

  it('snaps a weight to the nearest one the family ships', () => {
    expect(nearestWeight(findFont('Anton')!, 700)).toBe(400);
    expect(nearestWeight(findFont('Chakra Petch')!, 660)).toBe(700);
  });
});

describe('design-system defaults', () => {
  const design = {
    fontFamily: 'Inter',
    fontWeight: 700,
    color: '#123456',
    strokeColor: '#654321',
    warnings: ['font_substituted:Helvetica->Inter'],
  };

  it('is byte-identical to the legacy output without defaults', () => {
    expect(defaultTextContent()).toEqual({
      text: 'Your title',
      fontFamily: 'Chakra Petch',
      fontWeight: 700,
      fontSize: 96,
      lineHeight: 1.05,
      letterSpacing: 0,
      align: 'left',
      color: '#ffffff',
      stroke: { color: '#000000', width: 6 },
      shadow: { color: 'rgba(0,0,0,0.6)', blur: 18, offsetX: 0, offsetY: 8 },
      maxWidth: null,
      uppercase: false,
    });
    expect(defaultTextContent(null)).toEqual(defaultTextContent());
  });

  it('applies the brand font and colours only where the caller supplied none', () => {
    const content = defaultTextContent(design);
    expect(content.fontFamily).toBe('Inter');
    expect(content.color).toBe('#123456');
    expect(content.stroke?.color).toBe('#654321');
    // The response's substitution warnings and `strokeColor` are never text content.
    expect(content).not.toHaveProperty('warnings');
    expect(content).not.toHaveProperty('strokeColor');
  });

  it('keeps the legacy default for a null brand face or colour', () => {
    const content = defaultTextContent({ fontFamily: null, color: null, strokeColor: '#000000', warnings: [] });
    expect(content.fontFamily).toBe('Chakra Petch');
    expect(content.color).toBe('#ffffff');
  });

  it('lets an explicit override win over the brand default', () => {
    const content = defaultTextContent({ ...design, fontFamily: 'Anton', color: '#000000', stroke: null, text: 'Hi' });
    expect(content.fontFamily).toBe('Anton');
    expect(content.color).toBe('#000000');
    expect(content.stroke).toBeNull();
    expect(content.text).toBe('Hi');
  });
});
