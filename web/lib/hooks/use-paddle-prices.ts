'use client';

import { useEffect, useRef, useState } from 'react';
import { loadPaddle, requestPricePreview } from '../paddle/sdk';
import { previewItems } from '../paddle/pricing';
import type { BillingInterval, BillingPlan, PaddleClientConfig } from '../types/billing';

/** No totals yet, as one shared map so an empty result never re-renders on identity alone. */
const NO_TOTALS: Map<string, string> = new Map();

export interface PaddlePriceState {
  /** Price id → Paddle's own formatted total for that plan. Empty while unknown or unavailable. */
  totals: Map<string, string>;
  /** True from the moment a preview is asked for until it answers. */
  pending: boolean;
}

/**
 * What is being asked about, as one comparable string: the price ids, in order.
 *
 * A string rather than the array itself, deliberately. The plan list's identity is not a promise — a
 * caller that builds it inline (`data?.plans ?? []`) hands over a new array on every render — and
 * an effect keyed on that identity would ask Paddle for prices forever. The price ids are what the
 * answer depends on, so they are what the request is keyed on.
 */
function previewKey(plans: BillingPlan[], interval: BillingInterval): string {
  return previewItems(plans, interval).map((item) => `${item.priceId}:${item.quantity}`).join('|');
}

/**
 * Paddle's localized prices for the plans on sale, for DISPLAY only.
 *
 * Read-only by construction: the same call a storefront's own page makes to show a buyer their
 * country's price. It creates nothing, reserves nothing and grants nothing.
 *
 * Every outcome that is not a usable answer — no Paddle.js on this deployment, a refused call, an
 * offline buyer — leaves `totals` empty, and the caller then shows the catalogue's own list price
 * and says that is what it is. A price is never computed here: the strings in the map are Paddle's,
 * already localized and already formatted, and the page prints them unchanged.
 */
export function usePaddlePrices(
  config: PaddleClientConfig | null,
  plans: BillingPlan[],
  interval: BillingInterval = 'monthly',
): PaddlePriceState {
  const key = previewKey(plans, interval);
  // The latest list, so the effect below can read it without depending on its identity.
  const latestPlans = useRef(plans);
  useEffect(() => {
    latestPlans.current = plans;
  });

  const [state, setState] = useState<PaddlePriceState>({ totals: NO_TOTALS, pending: false });

  useEffect(() => {
    const items = previewItems(latestPlans.current, interval);
    if (!config || items.length === 0) {
      setState({ totals: NO_TOTALS, pending: false });
      return;
    }

    let cancelled = false;
    setState({ totals: NO_TOTALS, pending: true });
    void (async () => {
      const paddle = await loadPaddle(config);
      const totals = paddle ? await requestPricePreview(paddle, items) : null;
      // An answer that arrives after the plans on screen changed (or after unmount) is dropped: the
      // next run has already asked for what is showing now.
      if (!cancelled) setState({ totals: totals ?? NO_TOTALS, pending: false });
    })();

    return () => {
      cancelled = true;
    };
    }, [config, key, interval]);

  return state;
}
