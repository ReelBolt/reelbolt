import type { Metadata } from 'next';
import { getT, setRequestLocale } from '@/i18n/server';
import type { Locale } from '@/i18n/config';
import { Link } from '@/i18n/link';
import { Section } from '@/components/layout/Section';
import { Panel } from '@/components/ui/Panel';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Button } from '@/components/ui/Button';
import { Hero } from '@/components/home/Hero';
import { UseCaseStrip } from '@/components/home/UseCaseStrip';
import { AudiencePicker } from '@/components/audiences/AudiencePicker';
import { HowItWorks } from '@/components/audiences/HowItWorks';
import { BeforeAfter } from '@/components/proof/BeforeAfter';
import { FounderNote } from '@/components/proof/FounderNote';
import { SampleGallery } from '@/components/proof/SampleGallery';
import { Testimonials } from '@/components/proof/Testimonials';
import { TrustStrip } from '@/components/proof/TrustStrip';
import { Reveal } from '@/components/motion/Reveal';
import { JsonLd } from '@/components/seo/JsonLd';
import { buildMetadata } from '@/lib/seo';
import { faqSchema, softwareApplicationSchema } from '@/lib/structured-data';
import { HOME_FAQS } from '@/lib/faqs';
import { HOW_STEPS } from '@/lib/audiences/registry';
import { plans } from '@/lib/pricing';
import { joinNamesT } from '@/lib/pricing/localized';
import { localizedFaqs } from '@/lib/faq-localized';
import { useT } from '@/i18n';
import { siteConfig } from '@/lib/site-config';
import { PageMessages } from '@/components/layout/PageMessages';

export async function metadataFor(locale: string): Promise<Metadata> {
  const t = await getT('seo', locale as Locale);
  return buildMetadata({
    locale,
    title: t('pages.home.title'),
    // Two states, one source (`siteCtas`): the description is what search results and link
    // previews show, and it may say "Start free" only on a build where the page's button does that.
    description: siteConfig.signupEnabled ? t('pages.home.descriptionSignup') : t('pages.home.descriptionContact'),
    path: '/',
  });
}

// How many lines the catalogue holds under `home.page.beforeAfter.{before,after}.points`.
const BEFORE_POINTS = 3;
const AFTER_POINTS = 4;

// Icons for the six feature cards, in card order. Each one matches its card.
const ICON = [
  <path
    key="people"
    d="M16 11c1.7 0 3-1.3 3-3s-1.3-3-3-3-3 1.3-3 3 1.3 3 3 3Z M8 11c1.7 0 3-1.3 3-3S9.7 5 8 5 5 6.3 5 8s1.3 3 3 3Z M1 19c0-3 3.1-5 7-5s7 2 7 5 M13 19c0-3 3.1-5 7-5"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinecap="round"
    strokeLinejoin="round"
  />,
  <path
    key="footage"
    d="M4 7h16v10H4z M8 7v10 M16 7v10"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinecap="round"
  />,
  <path
    key="grid"
    d="M4 6h16v12H4z M4 10h16 M8 6v12 M16 6v12 M12 13.5v1"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinecap="round"
    strokeLinejoin="round"
  />,
  <path
    key="brand"
    d="M5 5h14v14H5z M9 9l7 3.5L9 16V9Z"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinejoin="round"
  />,
  <path
    key="voice"
    d="M12 3a3 3 0 0 1 3 3v6a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3Z M6 11a6 6 0 0 0 12 0 M12 17v4"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinecap="round"
    strokeLinejoin="round"
  />,
  <path
    key="ai"
    d="M12 3v4M12 17v4M3 12h4M17 12h4M6.3 6.3l2.8 2.8M14.9 14.9l2.8 2.8M17.7 6.3l-2.8 2.8M9.1 14.9l-2.8 2.8"
    stroke="currentColor"
    strokeWidth="1.75"
    strokeLinecap="round"
  />,
];

// Catalog-derived gate line. It names the plans from `lib/pricing`, never a typed plan name.

const features = [
  { key: 'team', icon: ICON[0] },
  { key: 'footage', icon: ICON[1] },
  { key: 'editor', icon: ICON[2] },
  { key: 'brand', icon: ICON[3] },
  { key: 'voice', icon: ICON[4] },
  { key: 'broll', icon: ICON[5] },
] as const;

function HomePageBody({ locale }: { locale: string }) {
  setRequestLocale(locale);
  const t = useT('home.page');
  const tp = useT('plans');
  const tf = useT('faq');
  const faqs = localizedFaqs(tf, 'home', HOME_FAQS.length);
  const videoGenPlans = joinNamesT(tp, plans.filter((plan) => plan.entitlements.videoGenAllowed).map((plan) => plan.name));
  return (
    <>
      <Hero />

      <div className="bg-paper">
        <div className="mx-auto w-full max-w-6xl px-4 sm:px-6 lg:px-8">
          <UseCaseStrip />
        </div>
      </div>

      <Reveal as="div">
        <AudiencePicker id="who" />
      </Reveal>

      <Reveal as="div">
        <BeforeAfter
          title={t('beforeAfter.title')}
          intro={t('beforeAfter.intro')}
          before={{
            summary: t('beforeAfter.before.summary'),
            points: Array.from({ length: BEFORE_POINTS }, (_, i) => t(`beforeAfter.before.points.${i}`)),
          }}
          after={{
            summary: t('beforeAfter.after.summary'),
            points: Array.from({ length: AFTER_POINTS }, (_, i) => t(`beforeAfter.after.points.${i}`)),
          }}
        />
      </Reveal>

      <Reveal as="div">
        <HowItWorks
          title={t('how.title')}
          intro={t('how.intro')}
          steps={HOW_STEPS.map((step) => ({
            title: t(`steps.${step}.title`),
            description: t(`steps.${step}.description`),
          }))}
        >
          <p>
            {t.rich('how.detail', {
              link: (chunks) => (
                <Link href="/features" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
              ),
            })}
          </p>
        </HowItWorks>
      </Reveal>

      <Reveal as="div">
        <TrustStrip />
      </Reveal>

      {/* Proof slots. Each renders nothing while its registry (`lib/proof/registry`) is empty,
          which is the case today: the trust strip above is the only proof the site shows. */}
      <SampleGallery />
      <Testimonials />
      <FounderNote />

      <Reveal as="div">
        <Section id="whats-inside">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
              {t('inside.title')}
            </h2>
          </div>
          <div className="mt-10 grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
            {features.map((feature) => (
              <Panel key={feature.key} className="p-6">
                <svg
                  width="28"
                  height="28"
                  viewBox="0 0 24 24"
                  fill="none"
                  aria-hidden="true"
                  focusable="false"
                  className="text-accent-ink"
                >
                  {feature.icon}
                </svg>
                <h3 className="mt-4 font-display text-base font-bold text-ink">{t(`features.${feature.key}.title`)}</h3>
                <p className="mt-2 text-sm text-ink-muted">{t(`features.${feature.key}.description`, { plans: videoGenPlans })}</p>
              </Panel>
            ))}
          </div>
          <p className="mt-10 text-center">
            <Link href="/features" className="font-medium text-accent-ink underline underline-offset-4">
              {t('inside.link')}
            </Link>
          </p>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="open-source" className="border-t border-line">
          <div className="mx-auto max-w-3xl text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
              {t('openSource.title')}
            </h2>
            <p className="mt-4 text-ink-muted">
              {t('openSource.body')}
            </p>
            <div className="mt-8 flex flex-col items-center justify-center gap-4 sm:flex-row">
              <Button href="/open-source" variant="outline" size="lg">
                {t('openSource.page')}
              </Button>
              <Button href={siteConfig.repoUrl} variant="ghost" size="lg">
                {t('openSource.github')}
              </Button>
            </div>
          </div>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="faq" className="bg-paper-2">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-ink sm:text-4xl">
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
          <p className="mt-8 text-center text-sm text-ink-muted">
            {t.rich('faq.deciding', {
              link: (chunks) => (
                <Link href="/about" className="font-medium text-accent-ink underline underline-offset-4">{chunks}</Link>
              ),
            })}
          </p>
        </Section>
      </Reveal>

      <Reveal as="div">
        <Section id="get-started" className="bg-ink text-paper">
          <div className="text-center">
            <h2 className="mt-3 font-display text-3xl font-bold text-paper sm:text-4xl">
              {t('start.title')}
            </h2>
            {/* Two states, one source (see `siteCtas`): with self-serve signup live the button
                really does start an account, so the body has to describe that rather than the
                demo call the button no longer is. */}
            <p className="mx-auto mt-3 max-w-xl font-sans text-sm text-paper/70">
              {siteConfig.signupEnabled
                ? t('start.bodySignup')
                : t('start.bodyContact')}
            </p>
            <div className="mt-8 flex flex-col items-center justify-center gap-4 sm:flex-row">
              <PrimaryCta location="home-get-started" variant="accent" size="lg" skew />
              <Button href="/pricing" variant="onInk" size="lg">
                {t('start.pricing')}
              </Button>
            </div>
          </div>
        </Section>
      </Reveal>

      <JsonLd data={softwareApplicationSchema(locale)} />
      <JsonLd data={faqSchema(faqs, locale)} />
    </>
  );
}

export function HomePage({ locale }: { locale: string }) {
  return (
    <PageMessages locale={locale} extra={['home']}>
      <HomePageBody locale={locale} />
    </PageMessages>
  );
}
