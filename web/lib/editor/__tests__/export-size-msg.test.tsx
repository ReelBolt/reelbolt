// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { useT } from '@/i18n';
import type { Msg } from '@/i18n';
import { estimateSizeText } from '../export-size';
import { estimateSizeMsg } from '../labels-msg';

afterEach(cleanup);
function Shown({ m }: { m: Msg }) {
  return <p data-testid="out">{useT().msg(m)}</p>;
}
const text = (m: Msg, locale: string) => {
  renderWithI18n(<Shown m={m} />, { locale });
  const out = screen.getByTestId('out').textContent;
  cleanup();
  return out;
};

describe('export size estimate as a message', () => {
  it.each([0, 1, 999, 1000, 54_321, 999_499, 1_000_000, 1_449_999, 1_450_000, 2_000_000, 12_345_678])('%i bytes reads the same in English as before', (bytes) => {
    // "1 bytes" is the one deliberate fix (plural); everything else is byte for byte the original.
    expect(text(estimateSizeMsg(bytes), 'en')).toBe(estimateSizeText(bytes).replace('about 1 bytes', 'about 1 byte'));
  });
  it('is formatted for the Italian locale', () => {
    expect(text(estimateSizeMsg(1_400_000), 'it')).toBe('circa 1,4 MB');
  });
});
