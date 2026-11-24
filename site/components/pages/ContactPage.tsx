import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import { Suspense } from 'react';
import { Container } from '@/components/layout/Container';
import { GridOverlay } from '@/components/ui/GridOverlay';
import { Panel } from '@/components/ui/Panel';
import { ContactForm } from '@/components/contact/ContactForm';
import { ContactAddress } from '@/components/contact/ContactAddress';
import { buildMetadata } from '@/lib/seo';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.contact.title'),
    description: t('pages.contact.description'),
    path: '/contact',
  });
}

function ContactPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('contact.page');
  return (
    <div className="relative isolate overflow-hidden py-16 sm:py-20 lg:py-28">
      <GridOverlay columns={3} className="hidden lg:block" />
      <Container>
        <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
          {t('title')}
        </h1>
        <p className="mt-4 max-w-2xl font-sans text-sm leading-relaxed text-ink-muted">
          {t('intro')}
        </p>

        <div className="mt-12 grid grid-cols-1 gap-10 lg:grid-cols-3 lg:gap-12">
          <div className="lg:col-span-2">
            {/* useSearchParams() in ContactForm needs a Suspense boundary to keep /contact static. */}
            <Suspense fallback={<div aria-hidden="true" className="min-h-[32rem]" />}>
              <ContactForm />
            </Suspense>
          </div>

          <aside className="lg:col-span-1">
            <Panel tone="paper-2" className="p-6">
              <h2 className="flex items-center gap-2 font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted">
                <span aria-hidden="true" className="h-2 w-2 shrink-0 bg-accent" />
                {t('otherWays')}
              </h2>
              <ContactAddress showPhone showResponseTime className="mt-4" />
            </Panel>
          </aside>
        </div>
      </Container>
    </div>
  );
}

export function ContactPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={['contact']}>
      <ContactPageBody locale={locale} />
    </PageMessages>
  );
}
