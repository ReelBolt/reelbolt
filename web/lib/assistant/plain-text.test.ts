import { describe, expect, it } from 'vitest';
import { plainText, withoutAttachId } from './plain-text';

const GUID = '12345678-1234-1234-1234-123456789012';

describe('plainText', () => {
  it('returns text with no id in it exactly as it came', () => {
    const text = 'Rename “Launch Teaser”  to “Spring Reel” .\n\nIt keeps its runs.  ';
    expect(plainText(text)).toBe(text);
  });

  it('removes an id and the gap it leaves', () => {
    expect(plainText(`Rename the project “${GUID}”`)).toBe('Rename the project');
    expect(plainText(`Renamed (${GUID}) just now.`)).toBe('Renamed just now.');
    expect(plainText(`It is ${GUID}.`)).toBe('It is.');
    expect(plainText(`${GUID}`)).toBe('');
  });

  it('finds an id whichever case it was written in, and every one of them', () => {
    expect(plainText(`${GUID.toUpperCase()} and ${GUID} are the same`)).toBe('and are the same');
  });

  it('keeps line breaks when it has to tidy a line', () => {
    expect(plainText(`First ${GUID}\n\nSecond`)).toBe('First\n\nSecond');
  });
});

describe('withoutAttachId', () => {
  it('drops only the attach message\'s id note', () => {
    expect(withoutAttachId(`I attached the file "a.mp4" (project file id ${GUID}). Use it.`)).toBe(
      'I attached the file "a.mp4". Use it.',
    );
  });

  it('leaves what a person typed alone, ids and all', () => {
    const typed = `Rename (project ${GUID}) please`;
    expect(withoutAttachId(typed)).toBe(typed);
  });
});
