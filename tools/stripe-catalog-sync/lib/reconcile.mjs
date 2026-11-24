import { MARKER_KEY, OWNER_KEY } from './catalog.mjs';
import { ConfigError, idempotencyKey } from './stripe-client.mjs';

// Item actions. DIFFERENT_ACTIONS count as "Stripe is not in sync" (dry-run exit 2);
// FAILED_ACTIONS are errors (exit 1); everything else is neutral.
export const DIFFERENT_ACTIONS = new Set(['create', 'update', 'replace-price', 'archive', 'drift', 'recreate']);
export const FAILED_ACTIONS = new Set(['not-ours', 'error']);

const ours = (object) => object?.metadata?.[MARKER_KEY] === '1';
const idOf = (ref) => (ref && typeof ref === 'object' ? ref.id : ref);

function item(kind, key, action, detail, extra = {}) {
  return { kind, key, action, detail, id: null, ...extra };
}

function metadataChanges(existing, desired) {
  const changed = {};
  for (const [k, v] of Object.entries(desired)) {
    if ((existing?.[k] ?? undefined) !== String(v)) {
      changed[k] = String(v);
    }
  }
  return changed;
}

/**
 * Reads Stripe (GET only) and decides what to do. Returns ordered items; each writable item
 * carries a `run(client)` closure that performs the writes and returns the object id.
 */
export async function buildPlan({ client, desired, options, promotions }) {
  const items = [];
  const productItems = new Map();

  for (const skipped of desired.skippedPlans) {
    items.push(item('plan', skipped.planId, 'skip', skipped.reason));
  }

  const productSpecs = [
    ...desired.plans.map((p) => ({
      productId: p.productId,
      name: `ReelBolt ${p.name}`,
      metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: p.productId, catalog_kind: p.kind, plan_key: p.planId },
    })),
    ...desired.packs.map((p) => ({
      productId: p.productId,
      name: `ReelBolt ${p.credits} credits`,
      metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: p.productId, catalog_kind: 'topup', credits: p.credits },
    })),
  ];

  const productStates = new Map();
  for (const spec of productSpecs) {
    const planned = await planProduct(client, spec, options);
    productItems.set(spec.productId, planned);
    productStates.set(spec.productId, planned.state);
    items.push(planned.item);
  }

  const priceSpecs = [
    ...desired.plans.map((p) => ({
      ...p,
      recurring: true,
      interval: 'month',
      metadata: {
        [MARKER_KEY]: '1',
        [OWNER_KEY]: p.lookupKey,
        plan_key: p.planId,
        ...(p.minSeats ? { min_seats: p.minSeats } : {}),
      },
    })),
    ...desired.plans
      .filter((p) => p.annual)
      .map((p) => ({
        ...p,
        lookupKey: p.annual.lookupKey,
        unitAmount: p.annual.unitAmount,
        recurring: true,
        interval: 'year',
        metadata: {
          [MARKER_KEY]: '1',
          [OWNER_KEY]: p.annual.lookupKey,
          plan_key: p.planId,
          ...(p.minSeats ? { min_seats: p.minSeats } : {}),
        },
      })),
    ...desired.packs.map((p) => ({
      ...p,
      recurring: false,
      metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: p.lookupKey, credits: p.credits },
    })),
  ];
  const priceItems = [];
  const knownLookupKeys = new Set(priceSpecs.map((s) => s.lookupKey));
  for (const spec of priceSpecs) {
    const productItem = productItems.get(spec.productId);
    const planned = await planPrice(client, spec, productItem, options, knownLookupKeys);
    priceItems.push(...planned);
  }
  items.push(...priceItems);

  if (promotions) {
    for (const skipped of promotions.skipped) {
      items.push(item('coupon', skipped.promotionId, 'skip', skipped.reason));
    }
    for (const promo of promotions.eligible) {
      items.push(await planCoupon(client, promo, productItems, options));
    }
  }

  if (!options.skipPortal) {
    items.push(await planPortal(client, options.siteUrl ?? DEFAULT_SITE_URL));
  }
  return items;
}

async function planProduct(client, spec, options) {
  const existing = await client.getOrNull(`/v1/products/${spec.productId}`);
  const key = spec.productId;
  if (!existing || existing.deleted) {
    const body = {
      id: spec.productId,
      name: spec.name,
      ...(options.taxCode ? { tax_code: options.taxCode } : {}),
      metadata: spec.metadata,
    };
    const detail = options.taxCode ? `name "${spec.name}", tax_code ${options.taxCode}` : `name "${spec.name}"`;
    const planned = item('product', key, 'create', detail, {
      run: async (c) => {
        const created = await c.request('POST', '/v1/products', { body, idempotencyKey: idempotencyKey('product.create', spec.productId, body) });
        return created.id;
      },
    });
    return { item: planned, state: 'new' };
  }
  if (!ours(existing)) {
    return {
      item: item('product', key, 'not-ours', `product exists without metadata ${MARKER_KEY}=1; refusing to touch it`, { id: existing.id }),
      state: 'not-ours',
    };
  }
  const body = {};
  const reasons = [];
  if (existing.name !== spec.name) {
    body.name = spec.name;
    reasons.push('name');
  }
  if (existing.active === false) {
    body.active = true;
    reasons.push('reactivate');
  }
  if (options.taxCode && existing.tax_code !== options.taxCode) {
    body.tax_code = options.taxCode;
    reasons.push('tax_code');
  }
  const meta = metadataChanges(existing.metadata, spec.metadata);
  if (Object.keys(meta).length > 0) {
    body.metadata = meta;
    reasons.push('metadata');
  }
  if (reasons.length === 0) {
    return { item: item('product', key, 'noop', 'in sync', { id: existing.id }), state: 'existing', existing };
  }
  return {
    item: item('product', key, 'update', reasons.join(', '), {
      id: existing.id,
      run: async (c) => {
        await c.request('POST', `/v1/products/${existing.id}`, { body, idempotencyKey: idempotencyKey('product.update', existing.id, body) });
        return existing.id;
      },
    }),
    state: 'existing',
    existing,
  };
}

function priceMatches(price, spec, productId) {
  if (!price.active || idOf(price.product) !== productId) {
    return false;
  }
  if (price.unit_amount !== spec.unitAmount || String(price.currency).toLowerCase() !== spec.currency) {
    return false;
  }
  if (spec.recurring) {
    return price.type === 'recurring' && price.recurring?.interval === (spec.interval ?? 'month') && (price.recurring?.interval_count ?? 1) === 1;
  }
  return price.type === 'one_time' && !price.recurring;
}

async function planPrice(client, spec, productPlan, options, knownLookupKeys = new Set()) {
  const key = spec.lookupKey;
  const dependsOn = [productPlan.item];
  if (productPlan.state === 'not-ours') {
    return [item('price', key, 'skip', 'its product is not ours', { dependsOn: [] })];
  }

  const found = await client.listAll('/v1/prices', { lookup_keys: [spec.lookupKey] });
  const foreign = found.find((p) => !ours(p));
  if (foreign) {
    return [item('price', key, 'not-ours', `price ${foreign.id} holds this lookup_key without metadata ${MARKER_KEY}=1; refusing to touch it`, { id: foreign.id })];
  }
  const holder = found.find((p) => p.active) ?? found[0] ?? null;

  if (holder && (holder.tax_behavior === 'inclusive' || holder.tax_behavior === 'exclusive') && holder.tax_behavior !== options.taxBehavior) {
    throw new ConfigError(
      `price ${holder.id} (${spec.lookupKey}) already has tax_behavior=${holder.tax_behavior}, which can never be changed; refusing to run with --tax-behavior ${options.taxBehavior}.`,
    );
  }

  const createBody = (extra) => ({
    product: spec.productId,
    currency: spec.currency,
    unit_amount: spec.unitAmount,
    lookup_key: spec.lookupKey,
    tax_behavior: options.taxBehavior,
    ...(spec.recurring ? { recurring: { interval: spec.interval ?? 'month' } } : {}),
    metadata: spec.metadata,
    ...extra,
  });
  const describe = `${spec.unitAmount} ${spec.currency}${spec.recurring ? `/${spec.interval ?? 'month'}` : ' one-time'}`;
  const out = [];
  let keptId = null;

  if (!holder) {
    const body = createBody({});
    out.push(
      item('price', key, 'create', describe, {
        dependsOn,
        run: async (c) => {
          const created = await c.request('POST', '/v1/prices', { body, idempotencyKey: idempotencyKey('price.create', spec.lookupKey, body) });
          return created.id;
        },
      }),
    );
  } else if (priceMatches(holder, spec, spec.productId)) {
    keptId = holder.id;
    const body = {};
    const reasons = [];
    if (holder.tax_behavior === 'unspecified' && options.taxBehavior !== 'unspecified') {
      body.tax_behavior = options.taxBehavior;
      reasons.push('tax_behavior');
    }
    const meta = metadataChanges(holder.metadata, spec.metadata);
    if (Object.keys(meta).length > 0) {
      body.metadata = meta;
      reasons.push('metadata');
    }
    if (reasons.length === 0) {
      out.push(item('price', key, 'noop', `${describe}, in sync`, { id: holder.id }));
    } else {
      out.push(
        item('price', key, 'update', reasons.join(', '), {
          id: holder.id,
          dependsOn,
          run: async (c) => {
            await c.request('POST', `/v1/prices/${holder.id}`, { body, idempotencyKey: idempotencyKey('price.update', holder.id, body) });
            return holder.id;
          },
        }),
      );
    }
  } else {
    const body = createBody({ transfer_lookup_key: true });
    const was = holder.unit_amount === null || holder.unit_amount === undefined ? 'a different shape' : `${holder.unit_amount} ${holder.currency}`;
    out.push(
      item('price', key, 'replace-price', `${was} -> ${describe}; old ${holder.id} is archived, subscribers keep it`, {
        dependsOn,
        supersedes: holder.id,
        run: async (c) => {
          const created = await c.request('POST', '/v1/prices', {
            body,
            idempotencyKey: idempotencyKey('price.create', spec.lookupKey, { body, supersedes: holder.id }),
          });
          if (holder.active) {
            const archive = { active: false };
            await c.request('POST', `/v1/prices/${holder.id}`, { body: archive, idempotencyKey: idempotencyKey('price.archive', holder.id, archive) });
          }
          return created.id;
        },
      }),
    );
  }

  // A crash between "create new price" and "archive old price" leaves an active price of ours
  // that no lookup_key points at. Find those on products we already own and archive them.
  if (productPlan.state === 'existing') {
    const active = await client.listAll('/v1/prices', { product: spec.productId, active: true });
    for (const stale of active) {
      // Never touch a price another spec owns (monthly vs yearly share a product), nor one of a
      // different interval or shape than this spec.
      const sameShape = spec.recurring ? stale.recurring?.interval === (spec.interval ?? 'month') : !stale.recurring;
      const claimed = stale.lookup_key && knownLookupKeys.has(stale.lookup_key);
      if (ours(stale) && sameShape && !claimed && stale.id !== keptId && stale.id !== holder?.id) {
        const archive = { active: false };
        out.push(
          item('price', `${key} (${stale.id})`, 'archive', 'active price of ours that no lookup_key points at', {
            id: stale.id,
            dependsOn,
            run: async (c) => {
              await c.request('POST', `/v1/prices/${stale.id}`, { body: archive, idempotencyKey: idempotencyKey('price.archive', stale.id, archive) });
              return stale.id;
            },
          }),
        );
      }
    }
  }
  return out;
}

function sameSet(a, b) {
  const x = [...(a ?? [])].sort();
  const y = [...(b ?? [])].sort();
  return x.length === y.length && x.every((v, i) => v === y[i]);
}

function couponDifferences(existing, want) {
  const diffs = [];
  const eq = (name, a, b) => {
    if ((a ?? null) !== (b ?? null)) {
      diffs.push(name);
    }
  };
  eq('percent_off', existing.percent_off, want.percent_off);
  eq('amount_off', existing.amount_off, want.amount_off);
  eq('currency', existing.currency ? String(existing.currency).toLowerCase() : null, want.currency);
  eq('duration', existing.duration, want.duration);
  eq('duration_in_months', existing.duration_in_months, want.duration_in_months);
  eq('max_redemptions', existing.max_redemptions, want.max_redemptions);
  eq('redeem_by', existing.redeem_by, want.redeem_by);
  if (!sameSet(existing.applies_to?.products, want.applies_to?.products)) {
    diffs.push('applies_to');
  }
  return diffs;
}

async function planCoupon(client, promo, productItems, options) {
  const { coupon } = promo;
  const key = promo.couponId;
  const blocked = coupon.applies_to.products.find((p) => productItems.get(p)?.state === 'not-ours');
  if (blocked) {
    return item('coupon', key, 'skip', `target product ${blocked} is not ours`);
  }
  const dependsOn = coupon.applies_to.products.map((p) => productItems.get(p)?.item).filter(Boolean);
  // Stripe leaves `applies_to` out of a coupon unless it is expanded (found in the sandbox).
  const existing = await client.getOrNull(`/v1/coupons/${coupon.id}`, { expand: ['applies_to'] });
  const createRun = (identity) => async (c) => {
    const created = await c.request('POST', '/v1/coupons', { body: coupon, idempotencyKey: idempotencyKey('coupon.create', identity, coupon) });
    return created.id;
  };
  const summary = `${coupon.percent_off !== undefined ? `${coupon.percent_off}% off` : `${coupon.amount_off} ${coupon.currency} off`}, ${coupon.duration}`;

  if (!existing || existing.deleted) {
    return item('coupon', key, 'create', summary, { dependsOn, run: createRun(coupon.id) });
  }
  if (!ours(existing)) {
    return item('coupon', key, 'not-ours', `coupon exists without metadata ${MARKER_KEY}=1; refusing to touch it`, { id: existing.id });
  }
  const diffs = couponDifferences(existing, coupon);
  if (diffs.length > 0) {
    if (options.recreateCoupons) {
      return item('coupon', key, 'recreate', `definition differs (${diffs.join(', ')}); delete and create again`, {
        dependsOn,
        run: async (c) => {
          await c.request('DELETE', `/v1/coupons/${coupon.id}`);
          return createRun(`${coupon.id}@${existing.created ?? 'prior'}`)(c);
        },
      });
    }
    return item('coupon', key, 'drift', `definition differs (${diffs.join(', ')}); coupons are immutable, decide by hand or pass --recreate-coupons`, { id: existing.id });
  }
  if (existing.name !== coupon.name) {
    const body = { name: coupon.name };
    return item('coupon', key, 'update', 'name', {
      id: existing.id,
      run: async (c) => {
        await c.request('POST', `/v1/coupons/${existing.id}`, { body, idempotencyKey: idempotencyKey('coupon.update', existing.id, body) });
        return existing.id;
      },
    });
  }
  return item('coupon', key, 'noop', 'in sync', { id: existing.id });
}

export const DEFAULT_SITE_URL = 'https://reelbolt.ai';
const PORTAL_CANCEL = { enabled: true, mode: 'at_period_end', proration_behavior: 'none' };
const PORTAL_UPDATES = ['address', 'email', 'name'];

/** What the portal shows about the business: a headline and the links to the legal pages. */
export function portalBusinessProfile(siteUrl) {
  const base = String(siteUrl).replace(/\/+$/, '');
  return {
    headline: 'Manage your ReelBolt plan, payment method and invoices',
    privacy_policy_url: `${base}/legal/privacy`,
    terms_of_service_url: `${base}/legal/terms`,
  };
}

export function portalBody(siteUrl = DEFAULT_SITE_URL) {
  return {
    business_profile: portalBusinessProfile(siteUrl),
    features: {
      invoice_history: { enabled: true },
      payment_method_update: { enabled: true },
      customer_update: { enabled: true, allowed_updates: PORTAL_UPDATES },
      subscription_cancel: PORTAL_CANCEL,
      subscription_update: { enabled: false },
    },
    metadata: { [MARKER_KEY]: '1', [OWNER_KEY]: 'portal' },
  };
}

function portalDifferences(existing, siteUrl) {
  const f = existing.features ?? {};
  const diffs = [];
  const wanted = portalBusinessProfile(siteUrl);
  const have = existing.business_profile ?? {};
  if (have.headline !== wanted.headline || have.privacy_policy_url !== wanted.privacy_policy_url || have.terms_of_service_url !== wanted.terms_of_service_url) diffs.push('business_profile');
  if (f.invoice_history?.enabled !== true) diffs.push('invoice_history');
  if (f.payment_method_update?.enabled !== true) diffs.push('payment_method_update');
  if (f.customer_update?.enabled !== true || !sameSet(f.customer_update?.allowed_updates, PORTAL_UPDATES)) diffs.push('customer_update');
  if (
    f.subscription_cancel?.enabled !== true ||
    f.subscription_cancel?.mode !== PORTAL_CANCEL.mode ||
    f.subscription_cancel?.proration_behavior !== PORTAL_CANCEL.proration_behavior
  ) {
    diffs.push('subscription_cancel');
  }
  if (f.subscription_update?.enabled !== false) diffs.push('subscription_update');
  if (existing.active === false) diffs.push('active');
  return diffs;
}

async function planPortal(client, siteUrl) {
  const all = await client.listAll('/v1/billing_portal/configurations');
  const tagged = all.filter((c) => c.metadata?.[OWNER_KEY] === 'portal');
  const foreign = tagged.find((c) => !ours(c));
  if (foreign) {
    return item('portal', 'portal', 'not-ours', `configuration ${foreign.id} is tagged ${OWNER_KEY}=portal without ${MARKER_KEY}=1; refusing to touch it`, { id: foreign.id });
  }
  const body = portalBody(siteUrl);
  const existing = tagged.find((c) => c.active !== false) ?? tagged[0];
  if (!existing) {
    return item('portal', 'portal', 'create', 'cancel at period end, no plan switching, no quantity changes', {
      run: async (c) => {
        const created = await c.request('POST', '/v1/billing_portal/configurations', { body, idempotencyKey: idempotencyKey('portal.create', 'portal', body) });
        return created.id;
      },
    });
  }
  const diffs = portalDifferences(existing, siteUrl);
  if (diffs.length === 0) {
    return item('portal', 'portal', 'noop', 'in sync', { id: existing.id });
  }
  const update = { ...body, active: true };
  return item('portal', 'portal', 'update', diffs.join(', '), {
    id: existing.id,
    run: async (c) => {
      await c.request('POST', `/v1/billing_portal/configurations/${existing.id}`, { body: update, idempotencyKey: idempotencyKey('portal.update', existing.id, update) });
      return existing.id;
    },
  });
}

/** Performs the writes. Items whose dependency failed are not attempted. */
export async function applyPlan(items, client) {
  for (const entry of items) {
    if (!entry.run) {
      continue;
    }
    const failedDependency = (entry.dependsOn ?? []).find((d) => d.error);
    if (failedDependency) {
      entry.error = `not attempted: ${failedDependency.kind} ${failedDependency.key} failed`;
      continue;
    }
    try {
      entry.id = await entry.run(client);
      entry.applied = true;
    } catch (error) {
      entry.error = String(error?.message ?? error).slice(0, 400);
    }
  }
}
