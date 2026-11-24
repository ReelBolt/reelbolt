import { CubeLutError, type CubeLutErrorCode, type CubeLutErrorDetails } from './cube-lut';
import { msg, type Msg } from '@/i18n/msg';

/** A refused LUT: a stable code plus the numbers or keyword the reason can name. `unreadable` = the file could not be fetched. */
export interface LutReason extends CubeLutErrorDetails {
  code: CubeLutErrorCode | 'unreadable';
}

export function lutReasonFromError(err: unknown): LutReason {
  return err instanceof CubeLutError ? { code: err.code, ...err.details } : { code: 'unreadable' };
}

const CODES: readonly CubeLutErrorCode[] = ['too_large', 'size_out_of_range', 'size_missing', 'wrong_row_count', 'not_finite', 'bad_domain', 'malformed', 'unsupported_keyword'];

/** Reads the engine's render-failure message (`LUT_INVALID: ...` / `LUT_UNSUPPORTED: ...`); null when it is not about a LUT. */
export function lutReasonFromFailureMessage(message: string | null | undefined): LutReason | null {
  if (!message || !/\bLUT_(INVALID|UNSUPPORTED)\b/.test(message)) return null;
  // The engine writes "(<code>, expected N, found M, keyword K)" after "cannot be used"; read only that part, so an
  // echoed token in the free text can never pick the code.
  const detail = /cannot be used \(([^)]*)\)/.exec(message)?.[1];
  if (detail === undefined) return { code: 'malformed' };
  const head = detail.split(',')[0]?.trim();
  const code = CODES.find((c) => c === head);
  if (!code) return { code: 'malformed' };
  const num = (key: string): number | undefined => {
    const m = new RegExp(`${key}\\s*[=:]?\\s*(\\d+)`, 'i').exec(detail);
    return m ? Number(m[1]) : undefined;
  };
  const expected = num('expected');
  const found = num('found');
  const keyword = /keyword\s*[=:]?\s*'?"?([A-Za-z0-9_]+)/i.exec(detail)?.[1];
  return { code, ...(expected !== undefined ? { expected } : {}), ...(found !== undefined ? { found } : {}), ...(keyword ? { keyword } : {}) };
}

/** One sentence per code, naming the numbers where known. Shared by the viewing LUT menu, the grade picker and the render failure. */
export function lutReasonMsg(reason: LutReason): Msg {
  switch (reason.code) {
    case 'too_large':
      return msg('editor-color.lutReason.tooLarge');
    case 'size_out_of_range':
      return msg('editor-color.lutReason.sizeOutOfRange');
    case 'size_missing':
      return msg('editor-color.lutReason.sizeMissing');
    case 'wrong_row_count':
      return reason.expected !== undefined && reason.found !== undefined
        ? msg('editor-color.lutReason.wrongRowCount', { expected: reason.expected, found: reason.found })
        : msg('editor-color.lutReason.wrongRowCountUnknown');
    case 'not_finite':
      return msg('editor-color.lutReason.notFinite');
    case 'bad_domain':
      return msg('editor-color.lutReason.badDomain');
    case 'unsupported_keyword':
      return reason.keyword ? msg('editor-color.lutReason.unsupportedKeyword', { keyword: reason.keyword }) : msg('editor-color.lutReason.unsupportedKeywordUnknown');
    case 'unreadable':
      return msg('editor-color.lutReason.unreadable');
    default:
      return msg('editor-color.lutReason.malformed');
  }
}
