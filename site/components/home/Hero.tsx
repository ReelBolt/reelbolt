import { useT } from '@/i18n';
import { Container } from '@/components/layout/Container';
import { GridOverlay } from '@/components/ui/GridOverlay';
import { Eyebrow } from '@/components/ui/Eyebrow';
import { Button } from '@/components/ui/Button';
import { PrimaryCta } from '@/components/ui/PrimaryCta';
import { Panel } from '@/components/ui/Panel';
import { JsonLd } from '@/components/seo/JsonLd';
import { ViewfinderMount } from '@/components/three/ViewfinderMount';
import { launchFilm } from '@/lib/launch-film';
import { videoObjectSchema } from '@/lib/video-schema';
import { siteConfig } from '@/lib/site-config';
import { LaunchFilm } from './LaunchFilm';
import { HeroGridParallax } from './HeroGridParallax';

// Server component: the only client-side pieces it renders are leaves (HeroGridParallax,
// PrimaryCta, the viewfinder mount and LaunchFilm), so no state or effects live here.
export function Hero() {
  const t = useT('home.hero');
  // Build-time config: the live launch film by default, or nothing at all when
  // `NEXT_PUBLIC_LAUNCH_FILM_BASE` is set to the empty string. Read here, on the server, so the
  // decision is baked into the static HTML and the hero never hydrates a different state.
  const film = launchFilm(process.env);
  return (
    <section className="relative isolate overflow-hidden bg-paper-2">
      <HeroGridParallax>
        <GridOverlay columns={4} horizontalAt={['62%']} />
      </HeroGridParallax>

      <Container>
        <div className="pt-6 pb-3 sm:pt-12 sm:pb-6 lg:pt-16">
          <h1 className="text-balance font-display text-[clamp(2.25rem,6.2vw,5.25rem)] font-bold leading-[1.0] text-ink">
            {t('title')}
          </h1>
          <Eyebrow className="mt-3 sm:mt-6">{t('eyebrow')}</Eyebrow>
        </div>

        <div className="grid gap-8 py-4 sm:gap-10 sm:py-10 lg:grid-cols-12 lg:py-12">
          <div className="flex flex-col lg:col-span-4 lg:col-start-1">
            <p className="order-2 mt-4 font-sans text-sm leading-relaxed text-ink-muted md:order-1 md:mt-0">
              {t('intro')}
            </p>
            {/* Below `md` the CTA block leads and the supporting paragraph follows: on a phone the
                consent bar is fixed to the bottom of the viewport, and with the paragraph first the
                CTA landed under it. `order` flips the visual order; the source order, the copy and
                both destinations are unchanged, and from `md` up the desktop order is restored. */}
            <div className="order-1 md:order-2 md:mt-8">
              <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
                <PrimaryCta location="hero" variant="accent" size="lg" skew />
                <Button href="/features" variant="outline">
                  {t('seeFeatures')}
                </Button>
              </div>
              {/* Only while the button really starts a free account (`siteCtas`): with signup off
                  it leads to the contact page, and "no card" would describe nothing. */}
              {siteConfig.signupEnabled && (
                <p className="mt-3 font-mono text-xs text-ink-muted">{t('support')}</p>
              )}
            </div>
          </div>

          {/* The film is the hero's dominant element: eight of twelve columns on `lg`, and bled to
              the viewport edge below it. The negative margins pull the figure out of `Container`'s
              own padding at each breakpoint; `lg:mx-0` hands it back to the grid once the two
              columns are side by side. */}
          <div className="-mx-4 sm:-mx-6 lg:col-span-8 lg:col-start-5 lg:mx-0">
            {film ? (
              <LaunchFilm film={film} />
            ) : (
              <Panel tone="ink" brackets className="aspect-video">
                <Eyebrow className="p-3 text-paper/70">{t('liveRender')}</Eyebrow>
                <div className="h-[calc(100%-2.5rem)] w-full">
                  <ViewfinderMount />
                </div>
              </Panel>
            )}
          </div>
        </div>
      </Container>

      {film && <JsonLd data={videoObjectSchema(film, siteConfig.url)} />}
    </section>
  );
}
