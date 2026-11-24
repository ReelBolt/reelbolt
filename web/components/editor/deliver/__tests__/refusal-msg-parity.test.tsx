// @vitest-environment jsdom
/** The catalogue (Msg) wording of a refused render equals the original English helper, byte for byte, and the Italian keeps the feature name. */
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { useT } from '@/i18n';
import { ApiError } from '@/lib/api/client';
import { describeRenderRefusal, describeRenderRefusalMsg } from '../render-refusal';

afterEach(cleanup);

function Shown({ error }: { error: unknown }) {
  const t = useT();
  const m = describeRenderRefusalMsg(error).message;
  return <p data-testid="out">{typeof m === 'string' ? m : t.msg(m)}</p>;
}
const textFor = (error: unknown, locale: string): string => {
  renderWithI18n(<Shown error={error} />, { locale });
  const out = screen.getByTestId('out').textContent ?? '';
  cleanup();
  return out;
};
const err402 = (body: Record<string, unknown>) => new ApiError('Payment Required', 402, [], body);

describe('402 plan refusal', () => {
  it.each([{ feature: 'HDR export' }, {}])('English output equals the original helper (%o)', (body) => {
    const error = err402(body);
    expect(textFor(error, 'en')).toBe(describeRenderRefusal(error).message);
  });

  it('keeps the feature name in Italian', () => {
    expect(textFor(err402({ feature: 'HDR export' }), 'it')).toContain('HDR export');
  });
});
