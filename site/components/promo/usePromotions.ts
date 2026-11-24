'use client';

import { useEffect, useMemo, useState } from 'react';
import { parseActivePromotions, type ActivePromotion } from '@/lib/promotions/active';
import { parsePlanPrices, type PlanPrice } from '@/lib/promotions/plan-prices';

const ENDPOINT = '/api/promotions';
const CLIENT_TIMEOUT_MS = 5000;
// setTimeout stores its delay as a signed 32-bit integer; a longer wait is re-armed when it fires.
const MAX_TIMER_MS = 2_147_483_647;

// One request per page load, shared by every hook instance (the banner, each pricing badge and each
// pricing price). It resolves to null on any failure, so the hooks simply stay empty.
let shared: Promise<unknown> | null = null;
function requestPromotions(): Promise<unknown> {
  shared ??= fetch(ENDPOINT, {
    headers: { accept: 'application/json' },
    signal: AbortSignal.timeout(CLIENT_TIMEOUT_MS),
  })
    .then((res) => (res.ok ? res.json() : null))
    .catch(() => null);
  return shared;
}

type Endable = { endsAt: string | null };

/**
 * Reads the shared body into one list, and drops each entry as its `endsAt` passes: one timer, armed
 * for the earliest end still in the list. `parse` must be a module-level function, so it is stable.
 */
function useTimedList<T extends Endable>(parse: (json: unknown, now: Date) => T[]): T[] {
  const [items, setItems] = useState<T[]>([]);
  // The clock the list is filtered against. 0 at first, so everything parsed is shown until a timer
  // fires; the timer only exists for items that end in the future.
  const [now, setNow] = useState(0);

  useEffect(() => {
    let cancelled = false;
    requestPromotions().then((json) => {
      if (!cancelled) setItems(parse(json, new Date()));
    });
    return () => {
      cancelled = true;
    };
  }, [parse]);

  const visible = useMemo(
    () => items.filter((p) => p.endsAt === null || Date.parse(p.endsAt) > now),
    [items, now],
  );

  useEffect(() => {
    let next: number | null = null;
    for (const p of visible) {
      if (p.endsAt === null) continue;
      const ends = Date.parse(p.endsAt);
      if (next === null || ends < next) next = ends;
    }
    if (next === null) return;
    const delay = Math.min(Math.max(next - Date.now(), 0), MAX_TIMER_MS);
    const timer = setTimeout(() => setNow(Date.now()), delay);
    return () => clearTimeout(timer);
  }, [visible]);

  return visible;
}

/** The body's `planPrices` re-read as the upstream `{ items }` shape the parser takes. */
function parsePlanPricesBody(json: unknown, now: Date): PlanPrice[] {
  const planPrices = typeof json === 'object' && json !== null ? (json as { planPrices?: unknown }).planPrices : undefined;
  return parsePlanPrices({ items: planPrices }, now);
}

/** The live promotions for this page. Empty until the request resolves, and on any error. */
export function usePromotions(): ActivePromotion[] {
  return useTimedList(parseActivePromotions);
}

/** The live plan prices for this page. Empty until the request resolves, and on any error. */
export function usePlanPrices(): PlanPrice[] {
  return useTimedList(parsePlanPricesBody);
}
