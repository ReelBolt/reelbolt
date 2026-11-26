import type { Metadata } from 'next';
import type { ReactNode } from 'react';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { GitHubLink } from '@/components/ui/GitHubLink';
import { Panel } from '@/components/ui/Panel';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { ComparisonTable, type ComparisonRow } from '@/components/blog/ComparisonTable';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema, faqSchema } from '@/lib/structured-data';
import { siteConfig, productVersion, RELEASE_URL } from '@/lib/site-config';
import { OPEN_SOURCE_FAQS } from '@/lib/open-source-faqs';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.openSource.title'),
    description: t('pages.openSource.description'),
    path: '/open-source',
  });
}

const REPO = siteConfig.repoUrl;
const BLOB = `${REPO}/blob/main`;
const LINK = 'font-medium text-accent-ink underline underline-offset-4';

const RUN_YOURSELF = ['0', '1', '2'] as const;
const ROWS = ['cost', 'setup', 'providers', 'updates', 'support', 'data', 'engine', 'name'] as const;
const STEPS = ['0', '1', '2'] as const;

function external(href: string) {
  return function ExternalAnchor(chunks: ReactNode) {
    return (
      <a href={href} rel="noopener" className={LINK}>
        {chunks}
      </a>
    );
  };
}

function OpenSourcePageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('openSource');

  const rows: ComparisonRow[] = ROWS.map((row) => ({
    label: t(`compare.rows.${row}.label`),
    cells: [
      t(`compare.rows.${row}.selfHost`),
      row === 'cost'
        ? { text: t(`compare.rows.${row}.hosted`), note: t(`compare.rows.${row}.hostedNote`) }
        : t(`compare.rows.${row}.hosted`),
    ],
  }));
  const faqs = OPEN_SOURCE_FAQS.map((_, i) => ({
    question: t(`faq.items.${i}.q`),
    answer: t(`faq.items.${i}.a`),
  }));

  return (
    <>
      <div className="border-b border-line pt-16 pb-12 sm:pt-20 lg:pt-24">
        <Container>
          <Eyebrow>{t('hero.eyebrow')}</Eyebrow>
          <h1 className="mt-4 max-w-4xl text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold uppercase leading-[0.95] tracking-[-0.02em] text-ink">
            {t('hero.title')}
          </h1>
          <p className="mt-5 max-w-2xl text-lg text-ink-muted">
            {t('hero.intro', { version: productVersion })}
          </p>
          <div className="mt-8 flex flex-col gap-4 sm:flex-row">
            <GitHubLink size="lg" />
            <PrimaryCta location="open-source-hero" variant="outline" size="lg" />
          </div>
          <p className="mt-4 text-sm">
            <a href={RELEASE_URL} rel="noopener" className={LINK}>
              {t('hero.release', { version: productVersion })}
            </a>
          </p>
        </Container>
      </div>

      <Reveal as="div">
        <Section className="border-b border-line">
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('run.title')}
          </h2>
          <dl className="mt-8 grid gap-x-10 gap-y-6 md:grid-cols-3">
            {RUN_YOURSELF.map((item) => (
              <div key={item} className="border-t-2 border-accent pt-4">
                <dt className="font-display text-lg font-bold text-ink">{t(`run.items.${item}.title`)}</dt>
                <dd className="mt-2 text-sm text-ink-muted">{t(`run.items.${item}.body`)}</dd>
              </div>
            ))}
          </dl>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section className="border-b border-line bg-paper-2">
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('compare.title')}
          </h2>
          <div className="mt-6 max-w-4xl">
            <ComparisonTable
              caption={t('compare.caption')}
              columns={[t('compare.selfHost'), t('compare.hosted')]}
              rows={rows}
            />
          </div>
          <p className="mt-6 max-w-3xl text-sm text-ink-muted">
            {t.rich('compare.note', {
              link: (chunks) => (
                <Link href="/pricing" className={LINK}>
                  {chunks}
                </Link>
              ),
            })}
          </p>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section className="border-b border-line">
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('start.title')}
          </h2>
          <ol className="mt-8 grid gap-4 md:grid-cols-3">
            {STEPS.map((step, index) => (
              <li key={step}>
                <Panel className="h-full p-6">
                  <span className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
                    {t('start.step', { n: index + 1 })}
                  </span>
                  <h3 className="mt-2 font-display text-lg font-bold text-ink">{t(`start.steps.${step}.title`)}</h3>
                  <p className="mt-2 text-sm text-ink-muted">{t(`start.steps.${step}.body`)}</p>
                </Panel>
              </li>
            ))}
          </ol>
          <p className="mt-6 max-w-3xl text-ink-muted">
            {t.rich('start.guide', {
              guide: external(`${BLOB}/docs/user-guide/self-hosting.md`),
              images: external(`${BLOB}/docs/self-hosting-release-images.md`),
            })}
          </p>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section className="border-b border-line bg-paper-2">
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('licence.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>{t.rich('licence.p1', { licence: external(`${BLOB}/LICENSE`) })}</p>
            <p>
              {t.rich('licence.p2', {
                mail: external('mailto:legal@reelbolt.ai'),
                commercial: external(`${BLOB}/COMMERCIAL.md`),
              })}
            </p>
            <p>{t.rich('licence.p3', { remotion: external('https://www.remotion.dev/docs/license') })}</p>
            <p>{t.rich('licence.p4', { trademark: external(`${BLOB}/TRADEMARK.md`) })}</p>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section className="border-b border-line">
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('contribute.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>
              {t.rich('contribute.p1', {
                guide: external(`${BLOB}/CONTRIBUTING.md`),
                issues: external(`${REPO}/issues`),
              })}
            </p>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="faq" className="bg-paper-2">
          <div className="text-center">
            <Eyebrow as="div" className="justify-center">
              {t('faq.eyebrow')}
            </Eyebrow>
            <h2 className="mt-3 font-display text-3xl font-bold uppercase tracking-tight text-ink sm:text-4xl">
              {t('faq.title')}
            </h2>
          </div>
          <dl className="mx-auto mt-10 max-w-3xl divide-y divide-line border-y border-line">
            {faqs.map((faq) => (
              <div key={faq.question} className="py-6">
                <dt className="font-display text-base font-bold text-ink">{faq.question}</dt>
                <dd className="mt-2 text-sm leading-relaxed text-ink-muted">{faq.answer}</dd>
              </div>
            ))}
          </dl>
        </Section>
      </Reveal>

      <JsonLd data={faqSchema(faqs, locale as Locale)} />
      <JsonLd
        data={breadcrumbSchema(
          [
            { name: t('breadcrumb.home'), path: '/' },
            { name: t('breadcrumb.openSource'), path: '/open-source' },
          ],
          locale,
        )}
      />
    </>
  );
}

export function OpenSourcePage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={[]}>
      <OpenSourcePageBody locale={locale} />
    </PageMessages>
  );
}
