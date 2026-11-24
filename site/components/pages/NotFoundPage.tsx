import type { Metadata } from 'next';
import { Container } from '@/components/layout/Container';
import { Button } from '@/components/ui/Button';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { useT } from '@/i18n';
import { getT, setRequestLocale } from '@/i18n/server';
import { buildMetadata } from '@/lib/seo';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('notfound.meta', locale);
  return buildMetadata({
    locale,
    title: t('title'),
    description: t('description'),
    path: '/404',
    noIndex: true,
  });
}

function NotFoundPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('notfound');
  return (
    <div className="py-16 text-center sm:py-20 lg:py-28">
      <Container>
        <p className="font-mono text-sm font-bold uppercase tracking-eyebrow text-accent-ink">404</p>
        <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
          {t('heading')}
        </h1>
        <p className="mx-auto mt-4 max-w-xl text-lg text-ink-muted">{t('body')}</p>

        <ul className="mx-auto mt-8 flex max-w-xl flex-col gap-3 sm:flex-row sm:flex-wrap sm:justify-center">
          <li>
            <Button href="/" variant="accent">
              {t('home')}
            </Button>
          </li>
          <li>
            <Button href="/features" variant="outline">
              {t('features')}
            </Button>
          </li>
          <li>
            <Button href="/pricing" variant="outline">
              {t('pricing')}
            </Button>
          </li>
          <li>
            <Button href="/blog" variant="outline">
              {t('blog')}
            </Button>
          </li>
          <li>
            <PrimaryCta location="not-found" variant="outline" />
          </li>
        </ul>
      </Container>
    </div>
  );
}

export function NotFoundPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={[]}>
      <NotFoundPageBody locale={locale} />
    </PageMessages>
  );
}
