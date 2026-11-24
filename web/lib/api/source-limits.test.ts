import { describe, expect, it } from 'vitest';
import IntlMessageFormat from 'intl-messageformat';
import type { T } from '@/i18n/translator';
import errorsIt from '@/messages/it/errors.json';
import failuresIt from '@/messages/it/failures.json';
import failuresEn from '@/messages/en/failures.json';
import { enT } from '../i18n-default';
import { ApiError } from './client';
import { describeError } from './describe-error';
import { sourceProblemMessage, sourceRefusalMessage } from './source-limits';
import { storageQuotaInfo, storageQuotaMessage } from './quota';
import { runFailureText } from '../utils/run-failure';

/** A minimal Italian translator over the real catalogue files. */
const ROOT = { errors: errorsIt, failures: failuresIt } as unknown as Record<string, unknown>;
function find(key: string): string | undefined {
  let node: unknown = ROOT;
  for (const part of key.split('.')) node = (node as Record<string, unknown> | undefined)?.[part];
  return typeof node === 'string' ? node : undefined;
}
const itT = Object.assign(
  (key: string, values?: Record<string, unknown>) =>
    new IntlMessageFormat(find(key) ?? key, 'it').format((values ?? {}) as Record<string, string | number>) as string,
  { has: (key: string) => find(key) !== undefined },
) as unknown as T;

const tooLarge = new ApiError('server english', 422, [], {
  code: 'SOURCE_TOO_LARGE', fileName: 'holiday.mp4', limitGb: 30, fileGb: 41,
});
const tooLong = new ApiError('server english', 422, [], {
  code: 'SOURCE_TOO_LONG', fileName: 'long.mp4', limitMinutes: 60, fileMinutes: 125,
});

describe('footage refusals', () => {
  it('say the file, whole GB and what to do, in English', () => {
    const text = sourceRefusalMessage(tooLarge, enT)!;
    expect(text).toBe('"holiday.mp4" is 41 GB, and the largest video we can edit is 30 GB. Trim it or export a smaller copy, then run again. Nothing was spent.');
    expect(text).not.toMatch(/byte|Max[A-Z]|SOURCE_/);
  });

  it('say whole minutes for a long video, in English', () => {
    expect(sourceRefusalMessage(tooLong, enT)).toBe(
      '"long.mp4" runs 125 minutes, and the longest video we can edit is 60 minutes. Trim it or export a shorter copy, then run again. Nothing was spent.',
    );
  });

  it('are written in Italian from the same figures', () => {
    expect(sourceRefusalMessage(tooLarge, itT)).toBe(
      '«holiday.mp4» pesa 41 GB, e il video più grande che possiamo modificare è di 30 GB. Accorcialo o esporta una copia più piccola, poi avvia di nuovo l\'esecuzione. Non è stato speso nulla.',
    );
    expect(sourceRefusalMessage(tooLong, itT)).toBe(
      '«long.mp4» dura 125 minuti, e il video più lungo che possiamo modificare è di 60 minuti. Accorcialo o esporta una copia più corta, poi avvia di nuovo l\'esecuzione. Non è stato speso nulla.',
    );
  });

  it('fall back to a figure-free sentence when the body has none', () => {
    expect(sourceProblemMessage({ code: 'SOURCE_TOO_LARGE' }, enT)).toMatch(/larger than we can edit/);
    expect(sourceProblemMessage({ code: 'SOURCE_TOO_LONG' }, itT)).toMatch(/più lungo/);
  });

  it('ignore any other error and are used by describeError', () => {
    expect(sourceRefusalMessage(new ApiError('x', 402, [], { code: 'INSUFFICIENT_CREDITS' }), enT)).toBeNull();
    expect(describeError(enT, tooLarge)).toContain('holiday.mp4');
  });
});

describe('a file bigger than the whole plan', () => {
  const body = { code: 'STORAGE_QUOTA_EXCEEDED', feature: 'storage', limit: 1073741824, current: 0, incoming: 3972844749, plan: 'trial' };
  const info = storageQuotaInfo(new ApiError('x', 413, [], body));

  it('says so and what to do, in English', () => {
    expect(storageQuotaMessage(info, enT)).toBe('This file is 3.7 GB and your Trial plan stores up to 1 GB. Export a smaller copy or upgrade.');
  });

  it('says so in Italian', () => {
    expect(storageQuotaMessage(info, itT)).toBe(
      'Questo file pesa 3.7 GB e il tuo piano Prova gratuita include fino a 1 GB. Esporta una copia più piccola o passa a un piano superiore.',
    );
  });

  it('keeps the ordinary full-workspace sentence when nothing is bigger than the plan', () => {
    const full = storageQuotaInfo(new ApiError('x', 413, [], { ...body, incoming: 500, current: 1073741000 }));
    expect(storageQuotaMessage(full, enT)).toMatch(/out of storage/);
  });
});

describe('old stored failures', () => {
  const old = 'Source video is 3951234567 bytes, exceeding MaxInputBytes=2000000000.';

  it('never show a byte count or a setting name', () => {
    const r = runFailureText(old, 'INPUT_TOO_LARGE');
    expect(r.summary).toBe(failuresEn.INPUT_TOO_LARGE);
    expect(r.summary).not.toMatch(/bytes|MaxInputBytes/);
    expect(r.details).toBeNull();
    expect(runFailureText('Source video duration 3000.00s exceeds MaxDurationSeconds=1800.', null).summary).toBe(failuresEn.INPUT_TOO_LARGE);
  });

  it('read in Italian through the catalogue', () => {
    expect(runFailureText(old, 'INPUT_TOO_LARGE', itT).summary).toBe(failuresIt.INPUT_TOO_LARGE);
  });

  it('have plain sentences for the new codes in both languages', () => {
    expect(failuresEn.SOURCE_TOO_LARGE).toMatch(/Trim it or export a smaller copy/);
    expect(failuresIt.SOURCE_TOO_LONG).toMatch(/Accorcialo/);
    expect(failuresEn.SCRATCH_BUSY).toMatch(/Try again/);
    expect(failuresIt.SCRATCH_BUSY).toMatch(/Riprova/);
  });
});

describe('too-demanding daily limit', () => {
  const limited = new ApiError('server english', 429, [], { code: 'TOO_DEMANDING_LIMIT', message: 'server english' });
  it('is the plain sentence in English', () => {
    expect(sourceRefusalMessage(limited, enT)).toBe(
      'This video has been too demanding for us to analyse several times today. Try a shorter clip or a smaller export, for example 1080p, or try again tomorrow. Nothing was spent.',
    );
  });
  it('is the plain sentence in Italian, without codes or settings', () => {
    const text = sourceRefusalMessage(limited, itT)!;
    expect(text).toBe(errorsIt.tooDemandingLimit);
    expect(text).not.toMatch(/TOO_DEMANDING|VideoSources|429/);
  });
});
