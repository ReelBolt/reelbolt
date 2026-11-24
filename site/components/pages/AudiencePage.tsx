import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import { getT, setRequestLocale } from '@/i18n/server';
import { prefixedLocales, type Locale } from '@/i18n/config';
import { useFormat, useT } from '@/i18n';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { PageMessages } from '@/components/layout/PageMessages';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { Button } from '@/components/ui/Button';
import { Panel } from '@/components/ui/Panel';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { AudiencePicker } from '@/components/audiences/AudiencePicker';
import { CapabilityList } from '@/components/audiences/CapabilityList';
import { HowItWorks } from '@/components/audiences/HowItWorks';
import { BeforeAfter } from '@/components/proof/BeforeAfter';
import { SampleGallery } from '@/components/proof/SampleGallery';
import { TrustStrip } from '@/components/proof/TrustStrip';
import { AUDIENCES, audienceForSlug, type Audience } from '@/lib/audiences/registry';
import { planNamesFor, trialFacts } from '@/lib/audiences/plan-gates';
import type { Faq } from '@/lib/faqs';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY_DAYS } from '@/lib/legal-placeholders';
import { joinNamesT } from '@/lib/pricing/localized';
import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import { breadcrumbSchema, faqSchema } from '@/lib/structured-data';

/** `generateStaticParams` for `app/(en)/for/[audience]`: exactly the six audience pages. */
export function audienceStaticParams(): { audience: string }[] {
  return AUDIENCES.map((audience) => ({ audience: audience.slug }));
}

/** The same six, once per prefixed language, for `app/[locale]/for/[audience]`. */
export function audienceLocaleStaticParams(): { locale: string; audience: string }[] {
  return prefixedLocales.flatMap((locale) => AUDIENCES.map((audience) => ({ locale, audience: audience.slug })));
}

export async function metadataFor(locale: string, slug: string): Promise<Metadata> {
  const audience = audienceForSlug(slug);
  if (!audience) notFound();
  const t = await getT('audiences', locale as Locale);
  return buildMetadata({
    locale,
    title: t(`items.${audience.messageKey}.seo.title`),
    description: t(`items.${audience.messageKey}.seo.description`),
    path: audience.path,
  });
}

const points = (count: number, read: (index: number) => string) => Array.from({ length: count }, (_, i) => read(i));

function AudiencePageBody({ locale, audience }: { locale: string; audience: Audience }) {
  setRequestLocale(locale);
  const t = useT('audiences');
  const tp = useT('plans');
  const f = useFormat();
  const item = `items.${audience.messageKey}`;
  const name = t(`${item}.name`);
  const trial = trialFacts();

  // Every figure and plan name an answer or a limit states is read here, from the plan catalog
  // and the first-video constant. Nothing is typed into the catalogue strings.
  const values = {
    credits: f.number(FIRST_VIDEO_GRANT_CREDITS),
    days: FIRST_VIDEO_GRANT_EXPIRY_DAYS,
    trialCredits: f.number(trial.credits),
    resolution: trial.resolution,
    videoGenPlans: joinNamesT(tp, planNamesFor('videoGen')),
    audioGenPlans: joinNamesT(tp, planNamesFor('audioGen')),
    ownProviderPlans: joinNamesT(tp, planNamesFor('ownProvider')),
  };

  // The audience's own questions, then the one every audience asks. The same array feeds the
  // visible list and the FAQPage markup, so the two cannot differ.
  //
  // "What does it cost to try?" answers with the free start (no card, the trial credits, the first
  // video on us), so it is asked only on a build where the page's button starts a free account
  // (`siteCtas`). With signup off the button leads to the contact page, and the answer would
  // describe a trial nobody can start from here; the same rule the hero's support line follows.
  const faqs: Faq[] = [
    ...Array.from({ length: audience.faqs }, (_, i) => ({
      question: t(`${item}.faq.${i}.q`, values),
      answer: t(`${item}.faq.${i}.a`, values),
    })),
    ...(siteConfig.signupEnabled ? [{ question: t('costFaq.q', values), answer: t('costFaq.a', values) }] : []),
  ];

  return (
    <>
      <div className="pt-10 pb-12 sm:pt-20 lg:pt-24 lg:pb-16">
        <Container>
          <div className="grid gap-10 lg:grid-cols-12">
            <div className="flex flex-col lg:col-span-7">
              <Eyebrow>{t('page.eyebrow', { audience: t(`${item}.audience`) })}</Eyebrow>
              <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[0.95] text-ink">
                {t(`${item}.hero.title`)}
              </h1>
              {/* Below `md` the CTA block leads and the intro follows, as in the home hero
                  (components/home/Hero.tsx): on a phone the consent bar is fixed to the bottom of
                  the viewport and the sticky CTA bar stays hidden until the visitor has answered
                  it, so with the paragraph first the page's only call to action sat under the bar.
                  These pages are where tracked links land. `order` flips the visual order only;
                  the source order and the copy are unchanged, and from `md` up the paragraph
                  comes first again. */}
              <p className="order-2 mt-6 max-w-2xl text-lg text-ink-muted md:order-1">{t(`${item}.hero.intro`)}</p>
              <div className="order-1 mt-6 md:order-2 md:mt-8">
                <PrimaryCta location={audience.cta.hero} persona={audience.persona} variant="accent" size="lg" skew />
                {siteConfig.signupEnabled && (
                  <p className="mt-3 font-mono text-xs text-ink-muted">{t('page.support')}</p>
                )}
              </div>
            </div>
            <div className="lg:col-span-5">
              <Panel tone="paper-2" brackets className="p-6 sm:p-8">
                <p className="text-sm text-ink-muted">{t(`${item}.who`)}</p>
                <p className="mt-4 font-display text-2xl font-bold leading-tight text-ink-2">
                  {t(`${item}.pain`)}
                </p>
                <p className="mt-4 text-accent-ink">{t(`${item}.outcome`)}</p>
              </Panel>
            </div>
          </div>
        </Container>
      </div>

      <Reveal as="div">
        <BeforeAfter
          title={t('page.beforeAfter.title')}
          before={{
            summary: t(`${item}.before.summary`),
            points: points(audience.beforePoints, (i) => t(`${item}.before.points.${i}`)),
          }}
          after={{
            summary: t(`${item}.after.summary`),
            points: points(audience.afterPoints, (i) => t(`${item}.after.points.${i}`)),
          }}
          persona={audience.persona}
        />
      </Reveal>

      <Reveal as="div">
        <CapabilityList audience={audience} />
      </Reveal>

      <Reveal as="div">
        <HowItWorks
          title={t('page.how.title')}
          steps={[
            { title: t('steps.drop.title'), description: t(`${item}.steps.drop`) },
            { title: t('steps.say.title'), description: t(`${item}.steps.say`) },
            { title: t('steps.review.title'), description: t('steps.review.description') },
          ]}
        />
      </Reveal>

      <Reveal as="div">
        <TrustStrip />
      </Reveal>

      <SampleGallery persona={audience.persona} />

      <Reveal as="div">
        <Section id="faq">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
              {t('page.faq.title')}
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

      <Reveal as="div">
        <AudiencePicker id="other-stories" exclude={audience.persona} />
      </Reveal>

      <Reveal as="div">
        <Section id="get-started" className="bg-ink text-paper">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-paper sm:text-4xl">
              {t(`${item}.final.title`)}
            </h2>
            {/* Two states, one source (`siteCtas`): the body describes what the button really does. */}
            <p className="mx-auto mt-3 max-w-xl text-paper/70">
              {siteConfig.signupEnabled ? t('page.final.bodySignup') : t('page.final.bodyContact')}
            </p>
            <div className="mt-8 flex flex-col items-center justify-center gap-4 sm:flex-row">
              <PrimaryCta location={audience.cta.final} persona={audience.persona} variant="accent" size="lg" skew />
              <Button href="/pricing" variant="onInk" size="lg">
                {t('page.final.pricing')}
              </Button>
            </div>
          </div>
        </Section>
      </Reveal>

      <JsonLd data={faqSchema(faqs, locale)} />
      <JsonLd
        data={breadcrumbSchema(
          [
            { name: t('page.breadcrumbHome'), path: '/' },
            { name, path: audience.path },
          ],
          locale,
        )}
      />
    </>
  );
}

export function AudiencePage({ locale, slug }: { locale: string; slug: string }) {
  const audience = audienceForSlug(slug);
  if (!audience) notFound();
  return (
    <PageMessages locale={locale} extra={[]}>
      <AudiencePageBody locale={locale} audience={audience} />
    </PageMessages>
  );
}
