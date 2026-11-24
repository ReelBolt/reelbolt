// In-memory Stripe: just enough of products, prices, coupons and billing portal
// configurations to exercise the sync tool. Implemented as a `fetch` so no socket is opened.

function decodeForm(text) {
  const root = {};
  for (const pair of text === '' ? [] : text.split('&')) {
    const [rawKey, rawValue = ''] = pair.split('=');
    const key = decodeURIComponent(rawKey.replace(/\+/g, ' '));
    const value = decodeURIComponent(rawValue.replace(/\+/g, ' '));
    const path = key.split(/[[\]]+/).filter(Boolean);
    let node = root;
    path.forEach((part, index) => {
      if (index === path.length - 1) {
        node[part] = value;
      } else {
        node[part] ??= {};
        node = node[part];
      }
    });
  }
  return normalize(root);
}

// Objects keyed 0..n-1 become arrays; "true"/"false" stay strings (like the real wire).
function normalize(node) {
  if (node && typeof node === 'object') {
    const keys = Object.keys(node);
    const out = keys.every((k, i) => k === String(i)) && keys.length > 0 ? keys.map((k) => normalize(node[k])) : Object.fromEntries(keys.map((k) => [k, normalize(node[k])]));
    return out;
  }
  return node;
}

const bool = (v) => (v === undefined ? undefined : v === true || v === 'true');
const num = (v) => (v === undefined ? undefined : Number(v));

export function createFakeStripe({ key = 'sk_test_fake', failures = [] } = {}) {
  const db = { products: new Map(), prices: new Map(), coupons: new Map(), portals: new Map() };
  const log = []; // every request
  const writes = []; // POST/DELETE only: { method, path, body }
  const violations = []; // protocol problems the real API would reject
  let counter = 0;
  const nextId = (prefix) => `${prefix}_${String((counter += 1)).padStart(4, '0')}`;
  const queuedFailures = [...failures];

  const json = (status, body, headers = {}) =>
    new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json', ...headers } });
  const missing = (what) => json(404, { error: { type: 'invalid_request_error', code: 'resource_missing', message: `No such ${what}` } });
  const bad = (message) => json(400, { error: { type: 'invalid_request_error', message } });

  function list(items, url) {
    const limit = Number(url.searchParams.get('limit') ?? 10);
    const after = url.searchParams.get('starting_after');
    let data = items;
    if (after) {
      data = data.slice(data.findIndex((x) => x.id === after) + 1);
    }
    return json(200, { object: 'list', data: data.slice(0, limit), has_more: data.length > limit });
  }

  const fetchImpl = async (input, init = {}) => {
    const url = new URL(String(input));
    const method = init.method ?? 'GET';
    const headers = Object.fromEntries(Object.entries(init.headers ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
    log.push({ method, host: url.host, protocol: url.protocol, path: url.pathname, query: url.search, headers });

    if (headers.authorization !== `Bearer ${key}`) {
      violations.push(`bad auth on ${method} ${url.pathname}`);
      return json(401, { error: { type: 'invalid_request_error', message: 'Invalid API Key' } });
    }
    if (headers['stripe-version'] !== '2026-09-30.endive') {
      violations.push(`bad Stripe-Version on ${method} ${url.pathname}`);
    }
    if (method !== 'GET' && method !== 'DELETE' && !headers['idempotency-key']) {
      violations.push(`missing Idempotency-Key on ${method} ${url.pathname}`);
    }
    const queued = queuedFailures.shift();
    if (queued) {
      return json(queued, { error: { type: 'api_error', message: 'transient' } }, queued === 429 ? { 'retry-after': '0' } : {});
    }

    const body = typeof init.body === 'string' ? decodeForm(init.body) : {};
    if (method !== 'GET') {
      writes.push({ method, path: url.pathname, body, idempotencyKey: headers['idempotency-key'] ?? null });
    }
    const parts = url.pathname.split('/').filter(Boolean); // v1, resource, id?
    const [, resource, id] = parts;

    if (resource === 'products') {
      if (method === 'GET' && id) {
        const p = db.products.get(id);
        return p ? json(200, p) : missing(`product '${id}'`);
      }
      if (method === 'POST' && !id) {
        if (db.products.has(body.id)) {
          return bad('Product already exists.');
        }
        const p = { object: 'product', id: body.id ?? nextId('prod'), name: body.name, active: true, tax_code: body.tax_code ?? null, metadata: body.metadata ?? {} };
        db.products.set(p.id, p);
        return json(200, p);
      }
      if (method === 'POST' && id) {
        const p = db.products.get(id);
        if (!p) return missing(`product '${id}'`);
        if (body.name !== undefined) p.name = body.name;
        if (body.active !== undefined) p.active = bool(body.active);
        if (body.tax_code !== undefined) p.tax_code = body.tax_code;
        p.metadata = { ...p.metadata, ...(body.metadata ?? {}) };
        return json(200, p);
      }
    }

    if (resource === 'prices') {
      if (method === 'GET' && !id) {
        let items = [...db.prices.values()];
        const lookups = url.searchParams.getAll('lookup_keys[0]');
        if (lookups.length > 0) items = items.filter((p) => lookups.includes(p.lookup_key));
        if (url.searchParams.has('product')) items = items.filter((p) => p.product === url.searchParams.get('product'));
        if (url.searchParams.has('active')) items = items.filter((p) => String(p.active) === url.searchParams.get('active'));
        return list(items, url);
      }
      if (method === 'POST' && !id) {
        if (!db.products.has(body.product)) return bad('No such product');
        const transfer = bool(body.transfer_lookup_key) === true;
        if (body.lookup_key) {
          const holder = [...db.prices.values()].find((p) => p.lookup_key === body.lookup_key);
          if (holder && !transfer) return bad('lookup_key is already used by another price');
          if (holder) holder.lookup_key = null;
        }
        const price = {
          object: 'price',
          id: nextId('price'),
          product: body.product,
          currency: body.currency,
          unit_amount: num(body.unit_amount),
          lookup_key: body.lookup_key ?? null,
          tax_behavior: body.tax_behavior ?? 'unspecified',
          active: true,
          type: body.recurring ? 'recurring' : 'one_time',
          recurring: body.recurring ? { interval: body.recurring.interval, interval_count: 1 } : null,
          metadata: body.metadata ?? {},
        };
        db.prices.set(price.id, price);
        return json(200, price);
      }
      if (method === 'POST' && id) {
        const price = db.prices.get(id);
        if (!price) return missing(`price '${id}'`);
        if (body.active !== undefined) price.active = bool(body.active);
        if (body.tax_behavior !== undefined) price.tax_behavior = body.tax_behavior;
        price.metadata = { ...price.metadata, ...(body.metadata ?? {}) };
        return json(200, price);
      }
    }

    if (resource === 'coupons') {
      if (method === 'GET' && id) {
        const c = db.coupons.get(id);
        if (!c) return missing(`coupon '${id}'`);
        // Like Stripe: `applies_to` is only returned when it is expanded.
        const expanded = url.searchParams.getAll('expand[0]').includes('applies_to') || url.searchParams.getAll('expand[]').includes('applies_to');
        const { applies_to, ...rest } = c;
        return json(200, expanded ? c : rest);
      }
      if (method === 'POST' && !id) {
        if (db.coupons.has(body.id)) return bad('Coupon already exists.');
        const c = {
          object: 'coupon',
          id: body.id,
          name: body.name ?? null,
          percent_off: num(body.percent_off) ?? null,
          amount_off: num(body.amount_off) ?? null,
          currency: body.currency ?? null,
          duration: body.duration,
          duration_in_months: num(body.duration_in_months) ?? null,
          max_redemptions: num(body.max_redemptions) ?? null,
          redeem_by: num(body.redeem_by) ?? null,
          applies_to: body.applies_to ? { products: body.applies_to.products } : undefined,
          created: 1760000000 + counter,
          metadata: body.metadata ?? {},
        };
        db.coupons.set(c.id, c);
        return json(200, c);
      }
      if (method === 'POST' && id) {
        const c = db.coupons.get(id);
        if (!c) return missing(`coupon '${id}'`);
        const editable = Object.keys(body).filter((k) => k !== 'name' && k !== 'metadata');
        if (editable.length > 0) return bad(`Coupon fields are immutable: ${editable.join(', ')}`);
        if (body.name !== undefined) c.name = body.name;
        return json(200, c);
      }
      if (method === 'DELETE' && id) {
        if (!db.coupons.delete(id)) return missing(`coupon '${id}'`);
        return json(200, { id, object: 'coupon', deleted: true });
      }
    }

    if (resource === 'billing_portal' && parts[2] === 'configurations') {
      const cid = parts[3];
      if (method === 'GET' && !cid) return list([...db.portals.values()], url);
      if (method === 'POST' && !cid) {
        const cfg = { object: 'billing_portal.configuration', id: nextId('bpc'), active: true, features: toWire(body.features), business_profile: body.business_profile ?? {}, metadata: body.metadata ?? {} };
        db.portals.set(cfg.id, cfg);
        return json(200, cfg);
      }
      if (method === 'POST' && cid) {
        const cfg = db.portals.get(cid);
        if (!cfg) return missing(`configuration '${cid}'`);
        if (body.features) cfg.features = toWire(body.features);
        if (body.business_profile) cfg.business_profile = { ...cfg.business_profile, ...body.business_profile };
        if (body.active !== undefined) cfg.active = bool(body.active);
        cfg.metadata = { ...cfg.metadata, ...(body.metadata ?? {}) };
        return json(200, cfg);
      }
    }
    return json(404, { error: { type: 'invalid_request_error', message: `Unrecognized request URL ${method} ${url.pathname}` } });
  };

  return {
    fetch: fetchImpl,
    key,
    db,
    log,
    writes,
    violations,
    seedProduct(product) {
      db.products.set(product.id, { object: 'product', active: true, tax_code: null, metadata: {}, ...product });
    },
    seedPrice(price) {
      const p = { object: 'price', id: nextId('price'), active: true, tax_behavior: 'exclusive', metadata: {}, ...price };
      db.prices.set(p.id, p);
      return p;
    },
    seedCoupon(coupon) {
      db.coupons.set(coupon.id, { object: 'coupon', metadata: {}, ...coupon });
    },
    seedPortal(cfg) {
      const c = { object: 'billing_portal.configuration', id: nextId('bpc'), active: true, features: {}, metadata: {}, ...cfg };
      db.portals.set(c.id, c);
      return c;
    },
    reset() {
      writes.length = 0;
      log.length = 0;
    },
  };
}

// The form wire turns booleans into strings; the real API returns real booleans.
function toWire(features = {}) {
  const out = {};
  for (const [name, value] of Object.entries(features)) {
    out[name] = Object.fromEntries(Object.entries(value).map(([k, v]) => [k, v === 'true' ? true : v === 'false' ? false : v]));
  }
  return out;
}
