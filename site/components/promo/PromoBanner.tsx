'use client';

import { Link } from '@/i18n/link';
import { useState } from 'react';
import { bannerPromotion, promoLabel, type ActivePromotion } from '@/lib/promotions/active';
import { useT } from '@/i18n';
import { usePromotions } from './usePromotions';

const DISMISS_KEY = 'reelbolt:promo-banner-dismissed';

// Identifies one promotion, so a dismissal hides that offer and a different one still shows.
function promoId(p: ActivePromotion): string {
  return [p.kind, p.endsAt ?? '', p.headline ?? '', p.percentOff ?? ''].join('|');
}

// Reads the dismissal during the first client render. The server and the first client render both have
// no promotions yet, so the bar renders nothing in both and hydration matches.
function readDismissed(): string | null {
  try {
    return window.sessionStorage.getItem(DISMISS_KEY);
  } catch {
    return null;
  }
}

/**
 * Site-wide promotions bar. It renders in normal flow above the sticky header (app/layout.tsx), so it
 * pushes the page down rather than covering it. It is one line on every width. With no promotion
 * (or the kill switch) nothing renders and no space is reserved. The kill switch is checked before
 * any hook runs, so with NEXT_PUBLIC_PROMO_BANNER=off nothing is fetched.
 */
export function PromoBanner() {
  if (process.env.NEXT_PUBLIC_PROMO_BANNER === 'off') return null;
  return <PromoBar />;
}

function PromoBar() {
  const t = useT('promo.banner');
  const promo = bannerPromotion(usePromotions());
  const [dismissedId, setDismissedId] = useState<string | null>(readDismissed);

  if (!promo || promoId(promo) === dismissedId) return null;

  const id = promoId(promo);
  const dismiss = () => {
    try {
      window.sessionStorage.setItem(DISMISS_KEY, id);
    } catch {
      // Storage blocked: the bar still hides for this view.
    }
    setDismissedId(id);
  };

  return (
    <div className="px-3 py-1 sm:px-6">
      <div
        role="region"
        aria-label={t('regionLabel')}
        className="mx-auto flex max-w-6xl items-center gap-2 border border-line-strong bg-paper-2 pl-3 text-ink"
      >
        <p
          title={promoLabel(promo)}
          className="min-w-0 flex-1 truncate py-1 font-mono text-xs text-ink-muted"
        >
          {promoLabel(promo)}
        </p>
        <Link
          href="/pricing"
          className="inline-flex min-h-11 shrink-0 items-center font-mono text-xs uppercase tracking-eyebrow text-accent-ink underline underline-offset-4 hover:text-accent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
        >
          {t('link')}
        </Link>
        <button
          type="button"
          onClick={dismiss}
          aria-label={t('dismiss')}
          className="inline-flex min-h-11 min-w-11 shrink-0 items-center justify-center text-base leading-none text-ink-muted hover:text-ink focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-strong"
        >
          <span aria-hidden="true">&times;</span>
        </button>
      </div>
    </div>
  );
}
