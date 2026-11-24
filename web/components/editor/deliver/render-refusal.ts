import { ApiError } from '@/lib/api/client';
import { msg, type Msg } from '@/i18n/msg';

/** A refused render start in plain words: one sentence, plus the server's own reasons when it gave any. */
export interface RenderRefusal {
  message: string;
  details: string[];
  /** Set for the not-enough-credits refusal: how many more credits the render needs (the panel offers "Add credits"). */
  missingCredits?: number;
  /** Set when the plan does not include what was asked for: the panel links to the plans. */
  planLink?: boolean;
}

/** The render request's field names (TimelineRenderStepConfig.Validate) as the form calls them. */
const FIELD_WORDS: [prefix: string, words: string][] = [
  ['outputFileName', 'The file name'],
  ['range.inFrame', 'The range start'],
  ['range.outFrame', 'The range end'],
  ['encode.width and encode.height', 'Width and height'],
  ['encode.width', 'The width'],
  ['encode.height', 'The height'],
  ['encode.codec', 'The codec'],
  ['encode.crf', 'The quality (CRF)'],
  ['encode.speed', 'The speed setting'],
  ['encode.audioBitrateKbps', 'The audio bitrate'],
  ['loudness.targetLufs', 'The loudness target'],
  ['loudness.truePeakDbtp', 'The true-peak ceiling'],
  ['loudness.loudnessRange', 'The loudness range'],
  ['preset', 'The format'],
  ['revision', 'The revision'],
];

/** Anything that reads like a serializer, a JSON path or a raw body, which a person should never see. */
const looksTechnical = (text: string): boolean =>
  /\$\.|JSON|System\.|Nullable`|BytePosition|^\s*[[{]|^[A-Za-z.]+:\s/.test(text);

const plain = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  const text = value.trim();
  return text !== '' && !looksTechnical(text) ? text : null;
};

const inWords = (reason: string): string => {
  for (const [prefix, words] of FIELD_WORDS) {
    if (reason.startsWith(`${prefix} `)) return `${words}${reason.slice(prefix.length)}`;
  }
  return reason;
};

const field = (body: unknown, key: string): unknown =>
  typeof body === 'object' && body !== null ? (body as Record<string, unknown>)[key] : undefined;

/**
 * What the Deliver panel says when `POST …/render` is refused. The server's own sentences are kept
 * (`{ message, errors }` from the request validator, the plan gate's `{ code, message }` for 402/429);
 * an ASP.NET binding ProblemDetails, a body that is not plain, or no body at all becomes a fixed
 * sentence for its status. Never a converter string, a JSON path or raw JSON.
 */
export function describeRenderRefusal(error: unknown): RenderRefusal {
  if (!(error instanceof ApiError)) {
    return { message: 'The render could not start. Check your connection and try again.', details: [] };
  }

  const ownMessage = plain(field(error.body, 'message'));
  switch (error.status) {
    case 400: {
      if (!ownMessage) {
        return { message: 'The render settings could not be read. Reload the page and try again.', details: [] };
      }
      const reasons = Array.isArray(field(error.body, 'errors')) ? (field(error.body, 'errors') as unknown[]) : [];
      const details = reasons
        .map((r) => (typeof r === 'string' ? inWords(r.trim()) : null))
        .filter((r): r is string => r !== null && plain(r) !== null);
      return { message: ownMessage, details };
    }
    case 402: {
      const missing = field(error.body, 'missing');
      if (field(error.body, 'code') === 'INSUFFICIENT_CREDITS') {
        const n = typeof missing === 'number' && Number.isFinite(missing) && missing > 0 ? Math.ceil(missing * 10) / 10 : null;
        return {
          message: n !== null ? `You need ${n} more ${n === 1 ? 'credit' : 'credits'} to render this.` : ownMessage ?? 'You do not have enough credits to render this.',
          details: [],
          ...(n !== null ? { missingCredits: n } : {}),
        };
      }
      const feature = plain(field(error.body, 'feature'));
      return {
        message: ownMessage ?? `Your plan does not include ${feature ? `this render option (${feature})` : 'this render format'}. Choose another format or pick a plan that does.`,
        details: [],
        planLink: true,
      };
    }
    case 403:
      return { message: ownMessage ?? 'You cannot start a render on this timeline.', details: [] };
    case 404:
      return { message: 'This timeline could not be found. It may have been deleted.', details: [] };
    case 429:
      return { message: ownMessage ?? 'Too many runs are going at once. Wait for one to finish, then start the render again.', details: [] };
    default:
      return {
        message: error.status >= 500
          ? 'The render could not start because of a problem on our side. Try again in a moment.'
          : ownMessage ?? 'The render could not start. Try again in a moment.',
        details: [],
      };
  }
}

/** {@link RenderRefusal} with the fixed sentences as Msgs; the server's own sentences stay strings. */
export interface RenderRefusalMsg {
  message: Msg | string;
  details: string[];
  missingCredits?: number;
  planLink?: boolean;
}

const R = 'editor-helpers-1.deliver.refusal';

/** {@link describeRenderRefusal} with its fixed sentences as catalogue Msgs (same branches, same order). */
export function describeRenderRefusalMsg(error: unknown): RenderRefusalMsg {
  if (!(error instanceof ApiError)) return { message: msg(`${R}.notConnected`), details: [] };
  const ownMessage = plain(field(error.body, 'message'));
  const base = describeRenderRefusal(error);
  switch (error.status) {
    case 400:
      return ownMessage ? base : { message: msg(`${R}.unreadable`), details: [] };
    case 402: {
      if (base.missingCredits !== undefined) return { message: msg(`${R}.needCredits`, { missing: base.missingCredits }), details: [], missingCredits: base.missingCredits };
      return { message: ownMessage ?? msg(field(error.body, 'code') === 'INSUFFICIENT_CREDITS' ? `${R}.notEnoughCredits` : `${R}.planLacks`, { feature: plain(field(error.body, 'feature')) ?? 'none' }), details: [], ...(base.planLink ? { planLink: true } : {}) };
    }
    case 403:
      return { message: ownMessage ?? msg(`${R}.forbidden`), details: [] };
    case 404:
      return { message: msg(`${R}.notFound`), details: [] };
    case 429:
      return { message: ownMessage ?? msg(`${R}.tooMany`), details: [] };
    default:
      return { message: error.status >= 500 ? msg(`${R}.serverError`) : ownMessage ?? msg(`${R}.startFailed`), details: [] };
  }
}
