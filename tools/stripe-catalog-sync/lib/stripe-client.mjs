import { createHash } from 'node:crypto';

export const STRIPE_API_VERSION = '2026-09-30.endive';
export const DEFAULT_API_BASE = 'https://api.stripe.com';
const MAX_RETRIES = 4;

export class ConfigError extends Error {
  constructor(message) {
    super(message);
    this.name = 'ConfigError';
  }
}

export class StripeApiError extends Error {
  constructor({ status, code, type, message, method, path }) {
    super(`${method} ${path} failed: HTTP ${status} ${code ?? type ?? 'error'}: ${message}`);
    this.name = 'StripeApiError';
    this.status = status;
    this.code = code ?? null;
    this.type = type ?? null;
  }
}

/** Mode from the key prefix only. Never returns or logs any other part of the key. */
export function parseKeyMode(key) {
  const match = /^(sk|rk)_(test|live)_/.exec(key ?? '');
  if (!match) {
    throw new ConfigError('STRIPE_API_KEY must be a secret (sk_) or restricted (rk_) key, test or live. Publishable keys (pk_) cannot be used.');
  }
  return { kind: match[1] === 'rk' ? 'restricted' : 'secret', mode: match[2] };
}

/** Only api.stripe.com (default) or a loopback host (stripe-mock) may receive the key. */
export function resolveApiBase(value) {
  const raw = value && value.trim() !== '' ? value.trim() : DEFAULT_API_BASE;
  let url;
  try {
    url = new URL(raw);
  } catch {
    throw new ConfigError('STRIPE_API_BASE is not a valid URL.');
  }
  const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  const official = url.hostname === 'api.stripe.com' && url.protocol === 'https:';
  if (!official && !(loopback && (url.protocol === 'http:' || url.protocol === 'https:'))) {
    throw new ConfigError(`STRIPE_API_BASE host "${url.hostname}" is refused: only api.stripe.com or localhost (stripe-mock) may receive the key.`);
  }
  return url.origin;
}

/** Stripe's nested form encoding: a[b]=1, a[0]=x, metadata[k]=v. Skips undefined and null. */
export function encodeForm(value, prefix = '', pairs = []) {
  if (value === undefined || value === null) {
    return pairs;
  }
  if (Array.isArray(value)) {
    value.forEach((item, index) => encodeForm(item, `${prefix}[${index}]`, pairs));
  } else if (typeof value === 'object') {
    for (const [key, inner] of Object.entries(value)) {
      encodeForm(inner, prefix === '' ? key : `${prefix}[${key}]`, pairs);
    }
  } else {
    pairs.push([prefix, String(value)]);
  }
  return pairs;
}

export function formString(value) {
  return encodeForm(value)
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
    .join('&');
}

/** Deterministic idempotency key: sha256 over operation, identity and desired state. */
export function idempotencyKey(operation, identity, state) {
  const digest = createHash('sha256')
    .update(JSON.stringify([operation, identity, canonical(state)]))
    .digest('hex');
  return `rbsync-${digest}`;
}

function canonical(value) {
  if (Array.isArray(value)) {
    return value.map(canonical);
  }
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map((k) => [k, canonical(value[k])]));
  }
  return value;
}

const defaultSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function createStripeClient({ key, baseUrl, fetchImpl = globalThis.fetch, sleep = defaultSleep, readOnly = false }) {
  parseKeyMode(key);
  const writes = [];

  async function request(method, path, { query, body, idempotencyKey: idemKey } = {}) {
    if (readOnly && method !== 'GET') {
      // Defence in depth: dry-run must never reach the network with a write.
      throw new Error(`refusing ${method} ${path} in read-only (dry-run) mode`);
    }
    let url = `${baseUrl}${path}`;
    const headers = {
      Authorization: `Bearer ${key}`,
      'Stripe-Version': STRIPE_API_VERSION,
      Accept: 'application/json',
    };
    const init = { method, headers, redirect: 'error' };
    if (method === 'GET') {
      const qs = formString(query);
      if (qs) {
        url += `?${qs}`;
      }
    } else if (method === 'POST') {
      if (!idemKey) {
        throw new Error(`POST ${path} requires an idempotency key`);
      }
      headers['Idempotency-Key'] = idemKey;
      headers['Content-Type'] = 'application/x-www-form-urlencoded';
      init.body = formString(body);
    }
    if (method !== 'GET') {
      writes.push({ method, path });
    }

    for (let attempt = 0; ; attempt += 1) {
      let response;
      try {
        response = await fetchImpl(url, init);
      } catch (error) {
        if (attempt >= MAX_RETRIES) {
          throw new Error(`${method} ${path} failed: network error (${error?.name ?? 'Error'})`);
        }
        await sleep(backoffMs(attempt));
        continue;
      }
      if ((response.status === 429 || response.status >= 500) && attempt < MAX_RETRIES) {
        await sleep(backoffMs(attempt, response.headers?.get?.('retry-after')));
        continue;
      }
      const text = await response.text();
      let payload = null;
      try {
        payload = text ? JSON.parse(text) : null;
      } catch {
        payload = null;
      }
      if (response.status >= 200 && response.status < 300) {
        return payload;
      }
      const error = payload?.error ?? {};
      throw new StripeApiError({
        status: response.status,
        code: error.code,
        type: error.type,
        message: String(error.message ?? 'no error body').slice(0, 300),
        method,
        path,
      });
    }
  }

  async function listAll(path, query = {}) {
    const items = [];
    let startingAfter;
    for (let page = 0; page < 50; page += 1) {
      const result = await request('GET', path, { query: { ...query, limit: 100, starting_after: startingAfter } });
      const data = result?.data ?? [];
      items.push(...data);
      if (!result?.has_more || data.length === 0) {
        return items;
      }
      startingAfter = data[data.length - 1].id;
    }
    throw new Error(`${path}: more than 5000 objects, refusing to continue`);
  }

  /** GET that maps 404 resource_missing to null. */
  async function getOrNull(path, query) {
    try {
      return await request('GET', path, { query });
    } catch (error) {
      if (error instanceof StripeApiError && error.status === 404) {
        return null;
      }
      throw error;
    }
  }

  return { request, listAll, getOrNull, writes };
}

function backoffMs(attempt, retryAfter) {
  const seconds = Number(retryAfter);
  if (Number.isFinite(seconds) && seconds > 0) {
    return Math.min(seconds, 30) * 1000;
  }
  return Math.min(500 * 2 ** attempt, 8000);
}
