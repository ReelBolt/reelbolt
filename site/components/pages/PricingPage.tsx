import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { useT, useFormat, useCurrentLocale } from '@/i18n';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Button } from '@/components/ui/Button';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { CreditsExplainer } from '@/components/pricing/CreditsExplainer';
import { FounderStrip } from '@/components/pricing/FounderStrip';
import { BillingSwitch } from '@/components/pricing/BillingSwitch';
import { PlanCard } from '@/components/pricing/PlanCard';
import { PlanComparison } from '@/components/pricing/PlanComparison';
import { buildMetadata } from '@/lib/seo';
import {
  FOUNDER_PERCENT_OFF,
  FOUNDER_PRICE_ENDS_AT,
  LAUNCH_OFFER,
  MONEY_BACK,
  MONEY_BACK_DAYS,
  founderPriceOpen,
  launchOfferCtaHref,
} from '@/lib/launch-offer';
import { breadcrumbSchema, faqSchema, pricingOffersSchema } from '@/lib/structured-data';
import { PRICING_FAQS } from '@/lib/faqs';
import { currency, lowPrice, plans, topUp } from '@/lib/pricing';
import { FIRST_VIDEO_GRANT_CREDITS } from '@/lib/legal-placeholders';
import { sharedPaidLines } from '@/lib/pricing/offers';
import { formatMoneyT, sharedPaidLinesT } from '@/lib/pricing/localized';
import { localizedFaqs } from '@/lib/faq-localized';
import { siteConfig } from '@/lib/site-config';
import { PageMessages } from '@/components/layout/PageMessages';

// No price appears in this metadata: it comes from the catalog at render time, and a number typed
// in here would be the one price on the page that drifts. The visible prices, the comparison table
// and the Offer markup all read `lib/pricing` instead.
export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.pricing.title'),
    description: t('pages.pricing.description'),
    path: '/pricing',
  });
}

function PricingPageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  // Printed once under the grid; each card leaves these lines out.
  const t = useT('pricing.page');
  const tp = useT('plans');
  const tf = useT('faq');
  const f = useFormat();
  const activeLocale = useCurrentLocale();
  const shared = sharedPaidLines(plans);
  const sharedShown = sharedPaidLinesT(tp, f, plans);
  const faqs = localizedFaqs(tf, 'pricing', PRICING_FAQS.length);
  // The English date keeps its day-month-year order; other locales use their own.
  const founderDate = new Intl.DateTimeFormat(activeLocale === 'en' ? 'en-GB' : activeLocale, {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'Europe/Rome',
  }).format(new Date(FOUNDER_PRICE_ENDS_AT));
  return (
    <>
      <div className="pt-16 pb-10 sm:pt-20 lg:pt-24">
        <Container>
          <h1 className="mt-4 text-balance font-display text-[clamp(2rem,5vw,3.5rem)] font-bold leading-[1.0] text-ink">
            {t('hero.title')}
          </h1>
          <p className="mt-4 max-w-2xl font-sans text-sm leading-relaxed text-ink-muted">
            {t('hero.intro')}
            {lowPrice !== undefined && <> {t('hero.from', { price: formatMoneyT(f, lowPrice, currency) })}</>}{' '}
            {t('taxNote')}
          </p>
          <p className="mt-3 max-w-2xl font-sans text-sm font-medium text-ink">{t('hero.firstVideo', { credits: f.number(FIRST_VIDEO_GRANT_CREDITS) })}</p>
        </Container>
      </div>

      {/* The launch offer. This band states the discount and nothing else: no price is written
          here, because every figure a reader sees still comes from the catalog below. The copy and
          the CTA destination live in `lib/launch-offer`, where the test runner can assert them —
          this `.tsx` file's own test could not run. The CTA is the site's one primary action, so
          the D25 switch decides whether it points at the signup route or the contact path. */}
      <FounderStrip openAtBuild={founderPriceOpen()}>
      <div id="launch-offer" className="bg-accent-tint">
        <Container>
          <div className="flex flex-col gap-6 py-8 sm:flex-row sm:items-center sm:justify-between sm:py-10">
            <div className="max-w-2xl">
              <Eyebrow>{t('offer.eyebrow')}</Eyebrow>
              <h2 className="mt-3 font-display text-2xl font-bold text-ink sm:text-3xl">
                {t('offer.heading', { percent: FOUNDER_PERCENT_OFF })}
              </h2>
              <p className="mt-2 font-sans text-sm leading-relaxed text-ink-muted">
                {t('offer.body', { date: founderDate })}
              </p>
              <p className="mt-1 font-sans text-xs text-ink-muted">
                {t('offer.until', { date: founderDate })}
              </p>
            </div>
            <PrimaryCta
              location={LAUNCH_OFFER.ctaLocation}
              href={launchOfferCtaHref(siteConfig.signupEnabled)}
              variant="accent"
              size="lg"
              skew
              className="shrink-0"
            />
          </div>
        </Container>
      </div>
      </FounderStrip>

      <Reveal as="div">
        <Section id="plans">
          <h2 className="sr-only">{t('plansHeading')}</h2>
          <div data-billing="monthly" className="group/plans">
          <BillingSwitch />
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
            {plans.map((plan) => (
              <PlanCard
                key={plan.id}
                plan={plan}
                currency={currency}
                signupEnabled={siteConfig.signupEnabled}
                sharedLines={shared}
              />
            ))}
          </div>
          </div>
          {shared.length > 0 && (
            <p className="mt-6 max-w-3xl font-sans text-xs text-ink-muted">
              {sharedShown.map((line) => `${line}.`).join(' ')}
            </p>
          )}
          <p className="mt-6 max-w-3xl text-sm font-medium text-ink">
            <Link href={MONEY_BACK.href} className="underline underline-offset-4">
              {t('moneyBack', { days: MONEY_BACK_DAYS })}
            </Link>
          </p>
          <p className="mt-8 max-w-3xl text-sm text-ink-muted">
            {t.rich('notSure', {
              link: (chunks) => (
                <Link href="/contact" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
              ),
            })}
          </p>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="credits" className="bg-paper-2">
          <CreditsExplainer currency={currency} topUp={topUp} />
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="compare">
          <h2 className="font-display text-2xl font-bold text-ink">
            {t('compare.title')}
          </h2>
          <p className="mt-4 max-w-3xl text-ink-muted">
            {t('compare.intro')}
          </p>
          <PlanComparison plans={plans} />
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="faq" className="bg-paper-2">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
              {t('faq.title')}
            </h2>
          </div>
          <dl className="mx-auto mt-10 max-w-3xl divide-y divide-line">
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
        <Section className="bg-ink text-paper">
          <div className="text-center">
            <h2 className="font-display text-2xl font-bold sm:text-3xl">
              {t('cta.title')}
            </h2>
            <p className="mx-auto mt-3 max-w-xl font-sans text-sm text-paper/70">
              {t('cta.body')}
            </p>
            <div className="mt-8 flex flex-col items-center justify-center gap-4 sm:flex-row">
              <PrimaryCta location="pricing" variant="accent" size="lg" skew />
              <Button href="/features" variant="onInk" size="lg">
                {t('cta.seeFeatures')}
              </Button>
            </div>
          </div>
        </Section>
      </Reveal>

      <JsonLd data={pricingOffersSchema(locale)} />
      <JsonLd data={faqSchema(faqs, locale)} />
      <JsonLd
        data={breadcrumbSchema([
          { name: t('breadcrumb.home'), path: '/' },
          { name: t('breadcrumb.pricing'), path: '/pricing' },
        ], locale)}
      />
    </>
  );
}

/** Namespaces the pricing page's client islands read with `useT` (BillingSwitch reads `plans.billing`). */
export const PRICING_CLIENT_NAMESPACES = ['plans'] as const;

export function PricingPage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={PRICING_CLIENT_NAMESPACES}>
      <PricingPageBody locale={locale} />
    </PageMessages>
  );
}
