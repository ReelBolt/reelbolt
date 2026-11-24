'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/lib/hooks/use-auth';
import { useT } from '@/i18n';
import { BrandMark } from '@/components/shell/BrandMark';
import { signInHref } from './marketplace-model';
import styles from './marketplace.module.css';

/**
 * The minimal public header of the community pages: the brand mark, then "Sign in" (and the sign-up call to action only when
 * self-serve sign-up is open, `signupEnabled` from the server) or, for a signed-in visitor, the way back to the dashboard.
 */
export function CommunityHeader({ signupEnabled }: { signupEnabled: boolean }) {
  const t = useT('marketplace.header');
  const pathname = usePathname();
  const { user } = useAuth();
  return (
    <header className={styles.header}>
      <Link href="/community" className={styles.brand} aria-label={t('home')}>
        <BrandMark size={28} />
        <span>{t('navLabel')}</span>
      </Link>
      <nav className={styles.headerActions} aria-label={t('navLabel')}>
        {user ? (
          <>
            <Link href="/community/mine" className={styles.button}>
              {t('mine')}
            </Link>
            <Link href="/dashboard" className={styles.button}>
            {t('dashboard')}
            </Link>
          </>
        ) : (
          <>
            <Link href={signInHref(pathname)} className={styles.button}>
              {t('signIn')}
            </Link>
            {signupEnabled && (
              <Link href="/signup" className={styles.buttonPrimary}>
                {t('signUp')}
              </Link>
            )}
          </>
        )}
      </nav>
    </header>
  );
}
