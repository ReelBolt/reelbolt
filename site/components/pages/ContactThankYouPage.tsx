import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import { Suspense } from 'react';
import { Container } from '@/components/layout/Container';
import { Button } from '@/components/ui/Button';
import { ContactAddress } from '@/components/contact/ContactAddress';
import { SubscribeNotice } from '@/components/contact/SubscribeNotice';
import { buildMetadata } from '@/lib/seo';
import { RESPONSE_TIME_SLA } from '@/lib/legal-placeholders';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.thanks.title'),
    description: t('pages.thanks.description'),
    path: '/contact/thank-you',
    noIndex: true,
  });
}

function ContactThankYouPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('contact.thanks');
  return (
    <div className="py-16 sm:py-20 lg:py-28">
      <Container>
        <div className="mx-auto max-w-2xl text-center">
          <h1 className="mt-4 text-balance font-display text-[clamp(1.75rem,4.5vw,3rem)] font-bold leading-[0.95] text-ink">
            {t('title')}
          </h1>
          <p className="mt-4 text-lg text-ink-muted">
            {t('body', { time: RESPONSE_TIME_SLA })}
          </p>
          <Suspense fallback={null}>
            <SubscribeNotice className="mt-4 text-lg text-ink-muted" />
          </Suspense>

          <div className="mt-10 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-center">
            <Button href="/" variant="accent" size="lg" skew>
              {t('home')}
            </Button>
            <Button href="/features" variant="outline" size="lg">
              {t('seeFeatures')}
            </Button>
          </div>
        </div>

        <div className="mx-auto mt-16 max-w-sm pt-8 text-center">
          <h2 className="font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">
            {t('sooner')}
          </h2>
          <ContactAddress showPhone showResponseTime className="mt-4 text-center" />
        </div>
      </Container>
    </div>
  );
}

export function ContactThankYouPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={['contact']}>
      <ContactThankYouPageBody locale={locale} />
    </PageMessages>
  );
}
