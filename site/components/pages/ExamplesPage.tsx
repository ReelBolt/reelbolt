import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Reveal } from '@/components/motion/Reveal';
import { LaunchFilm } from '@/components/home/LaunchFilm';
import { JsonLd } from '@/components/seo/JsonLd';
import { launchFilm } from '@/lib/launch-film';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema } from '@/lib/structured-data';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.examples.title'),
    description: t('pages.examples.description'),
    path: '/examples',
  });
}

const SHOWN_ITEMS = 4;

function ExamplesPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('examples');
  // The same film the homepage hero plays, read from the same build-time config. When it is
  // switched off (empty base) the page says so plainly instead of showing an empty frame.
  const film = launchFilm(process.env);
  return (
    <>
      <div className="border-b border-line pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <Eyebrow>{t('page.eyebrow')}</Eyebrow>
          <h1 className="mt-4 max-w-4xl text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold uppercase leading-[0.95] tracking-[-0.02em] text-ink">
            {t('page.title')}
          </h1>
          <p className="mt-5 max-w-2xl text-lg text-ink-muted">{t('page.lead')}</p>
          <div className="mt-8 flex flex-wrap items-center gap-4">
            <PrimaryCta location="examples_hero" variant="accent" size="lg" />
            <Link
              href="/features"
              className="inline-flex min-h-11 items-center font-medium text-accent-ink underline underline-offset-4"
            >
              {t('page.secondary')}
            </Link>
          </div>
        </Container>
      </div>

      <Section className="border-b border-line">
        {film ? (
          <LaunchFilm film={film} />
        ) : (
          <p className="max-w-2xl text-ink-muted">{t('film.unavailable')}</p>
        )}
        {film && <p className="mt-4 max-w-3xl text-sm text-ink-faint">{t('film.caption')}</p>}
      </Section>

      <Reveal as="div">
        <Section className="border-b border-line">
          <Eyebrow>{t('inside.eyebrow')}</Eyebrow>
          <h2 className="mt-3 font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('inside.title')}
          </h2>
          <div className="mt-4 max-w-3xl space-y-4 text-ink-muted">
            <p>{t('inside.intro')}</p>
            <ul className="space-y-2">
              {Array.from({ length: SHOWN_ITEMS }, (_, n) => (
                <li key={n} className="flex items-start gap-3">
                  <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 bg-accent" />
                  <span>{t(`inside.items.${n}`)}</span>
                </li>
              ))}
            </ul>
            <p className="text-sm text-ink-faint">{t('inside.note')}</p>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section>
          <h2 className="font-display text-2xl font-bold uppercase tracking-[-0.01em] text-ink">
            {t('next.title')}
          </h2>
          <p className="mt-4 max-w-2xl text-ink-muted">{t('next.body')}</p>
          <div className="mt-6 flex flex-wrap items-center gap-4">
            <PrimaryCta location="examples_footer" variant="accent" size="lg" />
            <Link
              href="/recipes"
              className="inline-flex min-h-11 items-center font-medium text-accent-ink underline underline-offset-4"
            >
              {t('next.recipes')}
            </Link>
          </div>
        </Section>
      </Reveal>

      <JsonLd
        data={breadcrumbSchema(
          [
            { name: t('breadcrumb.home'), path: '/' },
            { name: t('breadcrumb.examples'), path: '/examples' },
          ],
          locale,
        )}
      />
    </>
  );
}

export function ExamplesPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={[]}>
      <ExamplesPageBody locale={locale} />
    </PageMessages>
  );
}
