'use client';

import { Container } from '@/components/layout/Container';
import { Button } from '@/components/ui/Button';
import { buttonClass } from '@/components/ui/button-styles';
import { useT } from '@/i18n';

/** Body of both route error boundaries. Reads the chrome `layout` namespace, which the root shell always provides. */
export function RouteError({ reset }: { reset: () => void }) {
  const t = useT('layout.errorpage');
  return (
    <div className="py-16 text-center sm:py-20 lg:py-28">
      <Container>
        <h1 className="font-display text-3xl font-bold text-ink sm:text-4xl">{t('heading')}</h1>
        <p className="mx-auto mt-4 max-w-xl text-lg text-ink-muted">{t('body')}</p>
        <ul className="mx-auto mt-8 flex max-w-xl flex-col gap-3 sm:flex-row sm:flex-wrap sm:justify-center">
          <li>
            <button
              type="button"
              onClick={reset}
              className={buttonClass({ variant: 'accent' })}
            >
              {t('retry')}
            </button>
          </li>
          <li>
            <Button href="/" variant="outline">
              {t('home')}
            </Button>
          </li>
          <li>
            <Button href="/contact" variant="outline">
              {t('contact')}
            </Button>
          </li>
        </ul>
      </Container>
    </div>
  );
}
