import { siteConfig } from './site-config.ts';

/**
 * The live public base for the launch assets (D1a): a dedicated Cloudflare R2 bucket behind the
 * public subdomain `cdn.reelbolt.ai`, objects under the `launch/` prefix. It is the default so the
 * shipped page carries the film with no deploy change; `NEXT_PUBLIC_LAUNCH_FILM_BASE=''` disables
 * the film entirely.
 */
export const LAUNCH_FILM_BASE_DEFAULT = 'https://cdn.reelbolt.ai';

export interface LaunchFilmSource {
  src: string;
  type: string;
}

export interface LaunchFilmCaptions {
  src: string;
  srclang: string;
  label: string;
  default: boolean;
}

export interface LaunchFilm {
  sources: LaunchFilmSource[];
  poster: string | null;
  captions: LaunchFilmCaptions | null;
  width: number;
  height: number;
  title: string;
  description: string;
  durationIso: string | null;
  uploadDate: string | null;
}

type Env = Record<string, string | undefined>;

/** An unset var takes its default; an explicit empty string is kept, because empty means "off". */
function read(env: Env, key: string, fallback: string): string {
  const value = env[key];
  return value === undefined ? fallback : value;
}

/** Join a base and a path without ever doubling the slash. */
function join(base: string, path: string): string {
  return `${base.replace(/\/+$/, '')}/${path.replace(/^\/+/, '')}`;
}

/** A non-numeric or non-positive dimension falls back rather than producing NaN in the markup. */
function dimension(value: string | undefined, fallback: number): number {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

// A missing title or description falls back to a plain factual line, never to a claim about the
// video that nobody has verified. The description has to stay non-empty (the `VideoObject` needs
// it) but short — it is also the hero's one-line figcaption, and the site's 197-character meta
// description wrapped to five lines of small print under the film.
const DEFAULT_TITLE = `${siteConfig.name} launch film`;
const DEFAULT_DESCRIPTION = `The ${siteConfig.name} launch film.`;

/**
 * The launch film as the hero should render it, or `null` when no film is configured. Every path is
 * resolved against the base, so `sources[].src`, `poster` and `captions.src` are absolute. The
 * poster is `null` when `_POSTER_PATH` is empty — no poster file exists and one must not be
 * invented.
 */
export function launchFilm(env: Env): LaunchFilm | null {
  const base = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_BASE', LAUNCH_FILM_BASE_DEFAULT);
  if (base === '') return null;

  const srcPath = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_SRC_PATH', '/launch/launch-film-v4.mp4');
  const webmPath = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_WEBM_PATH', '');
  const posterPath = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_POSTER_PATH', '');
  const captionsPath = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_CAPTIONS_PATH', '');

  const sources: LaunchFilmSource[] = [];
  if (srcPath !== '') sources.push({ src: join(base, srcPath), type: 'video/mp4' });
  if (webmPath !== '') sources.push({ src: join(base, webmPath), type: 'video/webm' });

  // A base with no source at all is not a film: an empty `sources` array would render a `<video>`
  // that can never play, and `videoObjectSchema` reads `sources[0]`. Treat it as switched off, the
  // same as an empty base, so "no source" can never reach the renderer. (QA Q-T2.)
  if (sources.length === 0) return null;

  const duration = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_DURATION', 'PT1M1S');
  const uploadDate = read(env, 'NEXT_PUBLIC_LAUNCH_FILM_UPLOAD_DATE', '2026-10-20');

  return {
    sources,
    poster: posterPath === '' ? null : join(base, posterPath),
    captions:
      captionsPath === ''
        ? null
        : { src: join(base, captionsPath), srclang: 'en', label: 'English', default: false },
    width: dimension(env.NEXT_PUBLIC_LAUNCH_FILM_WIDTH, 1920),
    height: dimension(env.NEXT_PUBLIC_LAUNCH_FILM_HEIGHT, 1080),
    title: read(env, 'NEXT_PUBLIC_LAUNCH_FILM_TITLE', DEFAULT_TITLE),
    description: read(env, 'NEXT_PUBLIC_LAUNCH_FILM_DESCRIPTION', DEFAULT_DESCRIPTION),
    durationIso: duration === '' ? null : duration,
    uploadDate: uploadDate === '' ? null : uploadDate,
  };
}
