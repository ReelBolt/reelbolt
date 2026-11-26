import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { ContactAddress } from '@/components/contact/ContactAddress';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { siteConfig } from '@/lib/site-config';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema } from '@/lib/structured-data';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.about.title'),
    description: t('pages.about.description'),
    path: '/about',
  });
}

function AccentList({ items }: { items: string[] }) {
  return (
    <ul className="space-y-2">
      {items.map((item) => (
        <li key={item} className="flex items-start gap-3">
          <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 bg-accent" />
          <span>{item}</span>
        </li>
      ))}
    </ul>
  );
}

function AboutPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('about');
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            {t('page.title')}
          </h1>
        </Container>
      </div>

      <Reveal as="div">
        <Section>
          <h2 className="mt-3 font-display text-2xl font-bold text-ink">
            {t('mission.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>
              {t('mission.p1')}
            </p>
            <p>
              {t('mission.p2')}
            </p>
            <p>
              {t('mission.p3')}
            </p>
            <p>
              {t.rich('mission.p4', {
                link: (chunks) => (
                  <Link href="/features" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
                ),
              })}
            </p>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section tone="paper-2">
          <h2 className="mt-3 font-display text-2xl font-bold text-ink">
            {t('approach.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>
              {t('approach.p1')}
            </p>
            <p>
              {t('approach.p2')}
            </p>
            <AccentList
              items={[
                t('approach.l1'),
                t('approach.l2'),
                t('approach.l3'),
              ]}
            />
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section>
          <h2 className="mt-3 font-display text-2xl font-bold text-ink">
            {t('ownership.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>
              {t.rich('ownership.p1', {
                link: (chunks) => (
                  <Link href="/open-source" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
                ),
              })}
            </p>
            <p>
              {t('ownership.p2')}
            </p>
            <AccentList
              items={[
                t('ownership.l1'),
                t('ownership.l2'),
                t('ownership.l3'),
              ]}
            />
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section className="border-b border-line">
          <h2 className="mt-3 font-display text-2xl font-bold text-ink">
            {t('openSource.title')}
          </h2>
          <div className="mt-4 max-w-3xl text-ink-muted">
            <p>
              {t.rich('openSource.body', {
                link: (chunks) => (
                  <Link href="/open-source" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
                ),
                repo: (chunks) => (
                  <a
                    href={siteConfig.repoUrl}
                    rel="noopener"
                    className="font-medium text-accent-ink underline underline-offset-4"
                  >
                    {chunks}
                  </a>
                ),
              })}
            </p>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section>
          <h2 className="mt-3 font-display text-2xl font-bold text-ink">
            {t('contact.title')}
          </h2>
          <div className="mt-4 max-w-3xl text-ink-muted">
            <p>
              {t.rich('contact.body', {
                link: (chunks) => (
                  <Link href="/contact" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
                ),
              })}
            </p>
            <ContactAddress className="mt-4" />
          </div>
        </Section>
      </Reveal>

      <JsonLd
        data={breadcrumbSchema([
          { name: t('breadcrumb.home'), path: '/' },
          { name: t('breadcrumb.about'), path: '/about' },
        ], locale)}
      />
    </>
  );
}

export function AboutPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={[]}>
      <AboutPageBody locale={locale} />
    </PageMessages>
  );
}
