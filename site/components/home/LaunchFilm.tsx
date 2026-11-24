'use client';
import { useEffect, useRef, useState } from 'react';
import type { LaunchFilm as LaunchFilmConfig } from '@/lib/launch-film';
import { Panel } from '@/components/ui/Panel';
import { useReducedMotion } from '@/lib/use-reduced-motion';
import { shouldAutoplay, FILM_AUTOPLAY_THRESHOLD } from '@/lib/film-playback';

// Autoplay, muted, once the film scrolls into view. An IntersectionObserver starts it when it is on
// screen and pauses it when it leaves; a visitor who pauses it themselves is never restarted. Reduced
// motion is a hard opt-out — those visitors get the poster and the play affordance, never a playing
// 61-second film. Sound stays off on every path, including a click.
//
// `preload="metadata"` rather than the old `none`: the film now asks for itself the moment it is on
// screen, so withholding every byte would only delay a start the visitor did not have to request.
// `metadata` is the smallest value that lets playback begin promptly without pulling the whole file
// down while the poster is still the LCP. `auto` is refused: it competes with first paint for no
// benefit. The panel shows the design system's own ink frame with a play button and the film title
// until the film starts — which is also the state a missing poster degrades to, so there is never a
// broken image box.
export function LaunchFilm({ film }: { film: LaunchFilmConfig }) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const [started, setStarted] = useState(false);
  // A pause the visitor chose, so the observer must never restart it when the film scrolls back in.
  const userPausedRef = useRef(false);
  // Set around the observer's own off-screen pause, so `onPause` does not mistake it for a visitor.
  const selfPausedRef = useRef(false);
  const reduced = useReducedMotion();

  useEffect(() => {
    if (reduced) return;
    const video = videoRef.current;
    if (!video) return;
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (
          shouldAutoplay({
            reduced,
            intersecting: entry.isIntersecting,
            userPaused: userPausedRef.current,
          })
        ) {
          // The observer fires on every threshold crossing while the film intersects, not only when
          // a play is starting, so mute-and-play only when it is not already playing: a visitor who
          // unmuted mid-play must not be silently re-muted by an ordinary scroll.
          if (video.paused) {
            video.muted = true;
            void video.play().catch(() => {}); // a blocked autoplay leaves the affordance in place
          }
        } else if (!entry.isIntersecting && !video.paused) {
          selfPausedRef.current = true;
          video.pause();
        }
      },
      { threshold: FILM_AUTOPLAY_THRESHOLD },
    );
    observer.observe(video);
    return () => observer.disconnect();
  }, [reduced]);

  function start() {
    const video = videoRef.current;
    if (!video) return;
    // Same re-assert as the observer's: a visitor who unmuted and then clicks play must not get sound.
    video.muted = true;
    userPausedRef.current = false;
    void video.play().catch(() => {}); // a blocked play leaves the affordance in place
  }

  return (
    <figure className="m-0">
      <Panel tone="ink" brackets className="relative aspect-video overflow-hidden">
        <video
          ref={videoRef}
          className="absolute inset-0 h-full w-full object-contain"
          width={film.width}
          height={film.height}
          preload="metadata"
          controls={started}
          loop
          muted
          playsInline
          aria-label={film.title}
          poster={film.poster ?? undefined}
          onPlay={() => {
            userPausedRef.current = false;
            setStarted(true);
          }}
          onPause={() => {
            if (selfPausedRef.current) {
              selfPausedRef.current = false; // our own off-screen pause, not a visitor choice
              return;
            }
            userPausedRef.current = true;
          }}
        >
          {film.sources.map((source) => (
            <source key={source.src} src={source.src} type={source.type} />
          ))}
          {film.captions && (
            <track
              kind="captions"
              src={film.captions.src}
              srcLang={film.captions.srclang}
              label={film.captions.label}
              default={film.captions.default}
            />
          )}
        </video>

        {!started && (
          <button
            type="button"
            onClick={start}
            className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-ink/75 text-paper transition-colors hover:bg-ink/65 focus-visible:outline-2 focus-visible:outline-offset-[-4px] focus-visible:outline-accent-strong"
          >
            <span
              aria-hidden="true"
              className="flex h-14 w-14 items-center justify-center border border-paper/60"
            >
              <svg viewBox="0 0 24 24" className="h-5 w-5 translate-x-[1px] fill-current">
                <path d="M8 5v14l11-7z" />
              </svg>
            </span>
            <span className="font-mono text-[11px] uppercase tracking-eyebrow">{film.title}</span>
          </button>
        )}
      </Panel>
      {/* The hero bleeds this figure to the viewport edge below `lg`, so the caption re-applies the
          container's own horizontal padding to stay off the screen edge. */}
      <figcaption className="mt-2 px-4 font-mono text-[11px] leading-relaxed text-ink-muted sm:px-6 lg:px-0">
        {film.description}
      </figcaption>
    </figure>
  );
}
