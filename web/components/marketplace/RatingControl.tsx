'use client';

import { useState } from 'react';
import { IconStar, IconStarFilled } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { useMarketplaceActions } from '@/lib/hooks/use-marketplace';
import type { MarketplaceMe } from '@/lib/types/marketplace';
import { marketplaceErrorMessage } from './marketplace-model';
import styles from './marketplace.module.css';

const STARS = [1, 2, 3, 4, 5];

/** 1-5 stars. Enabled only when the account may rate (`canRate`); otherwise it says why. */
export function RatingControl({ recipeId, signedIn, me }: { recipeId: string; signedIn: boolean; me: MarketplaceMe | undefined }) {
  const t = useT('marketplace.rate');
  const tr = useT();
  const { rate } = useMarketplaceActions(recipeId);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const enabled = signedIn && me?.canRate === true && !busy;
  const mine = me?.myStars ?? 0;

  const choose = async (stars: number) => {
    if (!enabled) return;
    setBusy(true);
    setSaved(false);
    setError(null);
    try {
      await rate(stars);
      setSaved(true);
    } catch (err: unknown) {
      setError(marketplaceErrorMessage(tr, err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.section}>
      <h3 className={styles.promptHeading}>{t('title')}</h3>
      <div className={styles.stars} role="group" aria-label={t('group')}>
        {STARS.map((n) => (
          <button
            key={n}
            type="button"
            className={styles.starButton}
            disabled={!enabled}
            aria-pressed={mine >= n}
            aria-label={t('star', { stars: n })}
            onClick={() => void choose(n)}
          >
            {mine >= n ? <IconStarFilled size={24} aria-hidden /> : <IconStar size={24} aria-hidden />}
          </button>
        ))}
      </div>
      {!signedIn ? (
        <p className={styles.hint}>{t('signIn')}</p>
      ) : me && !me.canRate ? (
        <>
          <p className={styles.plain}>{t('runFirst')}</p>
          <p className={styles.hint}>{t('runFirstHint')}</p>
        </>
      ) : null}
      {mine > 0 && <p className={styles.hint}>{t('yours', { stars: mine })}</p>}
      {saved && <p className={styles.ok} role="status">{t('saved')}</p>}
      {error && <p className={styles.error} role="alert">{error}</p>}
    </div>
  );
}
