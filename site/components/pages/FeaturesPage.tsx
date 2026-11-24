import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import { docsHref, type Locale } from '@/i18n/config';
import { useT } from '@/i18n';
import type { ReactNode } from 'react';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { Figure } from '@/components/blog/Figure';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { buildMetadata } from '@/lib/seo';
import { breadcrumbSchema } from '@/lib/structured-data';
import { plans } from '@/lib/pricing';
import { highestResolutionPlan, planById, planMaxResolution, type BooleanEntitlement } from '@/lib/pricing/offers';
import { joinNamesT, storageLabelT } from '@/lib/pricing/localized';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.features.title'),
    description: t('pages.features.description'),
    path: '/features',
  });
}

// Every number and plan name on this page is read from the catalog through `lib/pricing`.
const trial = planById(plans, 'trial');
const team = planById(plans, 'team');
const namesWith = (key: BooleanEntitlement) => plans.filter((plan) => plan.entitlements[key]).map((plan) => plan.name);
const top = highestResolutionPlan(plans);

interface FeatureBlock {
  id: string;
  /** How many bullets and notes the catalogue holds under `blocks.<id>`. */
  bullets: number;
  notes?: number;
  /** A closing paragraph under the notes, for lines that carry links. */
  after?: boolean;
}

const BLOCKS: FeatureBlock[] = [
  { id: 'team', bullets: 4 },
  { id: 'rooms', bullets: 5, notes: 1 },
  { id: 'editing', bullets: 4 },
  { id: 'timeline', bullets: 5, notes: 2 },
  { id: 'pictures', bullets: 4, notes: 1 },
  { id: 'design', bullets: 4 },
  { id: 'graphics', bullets: 5 },
  { id: 'inserts', bullets: 4 },
  { id: 'privacy', bullets: 4 },
  { id: 'brand', bullets: 5, notes: 1 },
  { id: 'narration', bullets: 4 },
  { id: 'dubs', bullets: 3 },
  { id: 'b-roll', bullets: 4, notes: 1 },
  { id: 'quality', bullets: 3 },
  { id: 'repeat', bullets: 3 },
  { id: 'assistant', bullets: 5, notes: 1 },
  { id: 'sync', bullets: 4, notes: 1 },
  { id: 'teams', bullets: 4 },
  { id: 'own-machine', bullets: 4 },
  { id: 'resolution', bullets: 4 },
  { id: 'control', bullets: 5, after: true },
];

interface Band {
  id: string;
  /** Catalogue key under `bands`. */
  key: string;
  /** Ids from BLOCKS, in reading order. Each section id appears in exactly one band. */
  sections: string[];
  /** Screenshot for the empty right column on desktop, from public/blog/img. */
  figure: { name: string; width: number; height: number };
}

const BANDS: Band[] = [
  { id: 'band-make', key: 'make', sections: ['team', 'editing', 'quality', 'repeat'], figure: { name: 'workflow-builder', width: 1280, height: 624 } },
  { id: 'band-edit', key: 'edit', sections: ['timeline', 'pictures', 'graphics', 'design', 'brand', 'inserts', 'privacy'], figure: { name: 'timeline-editor', width: 1280, height: 800 } },
  { id: 'band-voice', key: 'voice', sections: ['narration', 'dubs', 'b-roll', 'rooms'], figure: { name: 'project-files', width: 1280, height: 800 } },
  { id: 'band-assistant', key: 'assistant', sections: ['assistant', 'resolution'], figure: { name: 'assistant', width: 1280, height: 800 } },
  { id: 'band-team', key: 'team', sections: ['teams', 'sync', 'own-machine', 'control'], figure: { name: 'brand-kit', width: 1280, height: 800 } },
];

function sectionsFor(ids: string[]): FeatureBlock[] {
  return ids.map((id) => {
    const block = BLOCKS.find((b) => b.id === id);
    if (!block) throw new Error(`Features band refers to unknown section "${id}"`);
    return block;
  });
}

function AccentList({ items }: { items: ReactNode[] }) {
  return (
    <ul className="space-y-2">
      {items.map((item, i) => (
        <li key={i} className="flex items-start gap-3">
          <span aria-hidden="true" className="mt-1.5 h-2 w-2 shrink-0 bg-accent" />
          <span>{item}</span>
        </li>
      ))}
    </ul>
  );
}

function FeaturesPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('features');
  const tp = useT('plans');
  const topName = (top?.entitlements.maxOutputHeight ?? 0);
  const values = {
    roomsPlans: joinNamesT(tp, namesWith('roomsAllowed')),
    videoGenPlans: joinNamesT(tp, namesWith('videoGenAllowed')),
    byoPlans: joinNamesT(tp, namesWith('byoAllowed')),
    storageFrom: storageLabelT(tp, trial) ?? '',
    storageFromPlan: trial.name,
    storageTo: storageLabelT(tp, team) ?? '',
    storageToPlan: team.name,
    topRes: top ? planMaxResolution(top) : '',
    topPlans: joinNamesT(
      tp,
      plans.filter((plan) => plan.entitlements.maxOutputHeight === topName).map((plan) => plan.name),
    ),
  };
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
            {t('page.title')}
          </h1>
          <p className="mt-4 max-w-2xl font-sans text-sm leading-relaxed text-ink-muted">
            {t('page.intro')}
          </p>
        </Container>
      </div>

      <nav
        aria-label={t('page.navLabel')}
        className="border-b border-line bg-paper lg:sticky lg:top-20 lg:z-30"
      >
        <Container>
          <ul className="flex gap-2 overflow-x-auto py-3">
            {BANDS.map((band) => (
              <li key={band.id} className="shrink-0">
                <a
                  href={`#${band.id}`}
                  className="block border border-line px-3 py-2 font-mono text-[11px] uppercase tracking-eyebrow text-ink-muted transition-colors hover:border-ink hover:text-ink focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
                >
                  {t(`bands.${band.key}.heading`)}
                </a>
              </li>
            ))}
          </ul>
        </Container>
      </nav>

      {BANDS.map((band, i) => (
        <Section
          key={band.id}
          id={band.id}
          tone={i % 2 === 1 ? 'paper-2' : undefined}
          className="scroll-mt-40"
        >
          <h2 className="mt-3 font-display text-2xl font-bold text-ink sm:text-3xl">
            {t(`bands.${band.key}.heading`)}
          </h2>
          <div className="mt-10 grid gap-12 lg:grid-cols-2 lg:gap-16">
            <div className="space-y-16">
              {sectionsFor(band.sections).map((block) => (
                <Reveal as="div" key={block.id}>
                  <div id={block.id} className="scroll-mt-40">
                    <h3 className="font-display text-xl font-bold text-ink">
                      {t(`blocks.${block.id}.heading`, values)}
                    </h3>
                    <div className="mt-4 space-y-4 text-ink-muted">
                      <p>{t(`blocks.${block.id}.intro`, values)}</p>
                      <AccentList items={Array.from({ length: block.bullets }, (_, n) => t(`blocks.${block.id}.bullets.${n}`, values))} />
                      {Array.from({ length: block.notes ?? 0 }, (_, n) => (
                        <p key={n} className="text-sm text-ink-faint">
                          {t(`blocks.${block.id}.notes.${n}`, values)}
                        </p>
                      ))}
                      {block.after && (
                        <p>
                          {t.rich(`blocks.${block.id}.after`, {
                            docs: (chunks) => (
                              <a href={docsHref(locale)} className="font-medium text-accent-ink underline underline-offset-4">
                                {chunks}
                              </a>
                            ),
                            about: (chunks) => (
                              <Link href="/about" className="font-medium text-accent-ink underline underline-offset-4">
                                {chunks}
                              </Link>
                            ),
                          })}
                        </p>
                      )}
                    </div>
                  </div>
                </Reveal>
              ))}
            </div>
            <div className="lg:sticky lg:top-40 lg:self-start">
              <Figure
                name={band.figure.name}
                alt={t(`bands.${band.key}.alt`)}
                width={band.figure.width}
                height={band.figure.height}
                sizes="(min-width: 1024px) 45vw, 100vw"
              />
            </div>
          </div>
        </Section>
      ))}

      <div className="bg-ink text-paper">
        <Container>
          <div className="flex flex-col items-center gap-6 px-6 py-16 text-center sm:px-12">
            <h2 className="font-display text-2xl font-bold sm:text-3xl">
              {t('cta.title')}
            </h2>
            <PrimaryCta location="features" variant="accent" size="lg" skew />
          </div>
        </Container>
      </div>

      <JsonLd
        data={breadcrumbSchema([
          { name: t('breadcrumb.home'), path: '/' },
          { name: t('breadcrumb.features'), path: '/features' },
        ], locale)}
      />
    </>
  );
}

export function FeaturesPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={[]}>
      <FeaturesPageBody locale={locale} />
    </PageMessages>
  );
}
