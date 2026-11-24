import type { LaunchFilm } from './launch-film.ts';

/** Make a URL absolute against the site, leaving an already-absolute one alone. */
function absolute(url: string, siteUrl: string): string {
  try {
    return new URL(url, siteUrl).href;
  } catch {
    return url;
  }
}

/**
 * The schema.org `VideoObject` for a configured launch film. `duration` and `uploadDate` are only
 * emitted when the film actually carries them, and `thumbnailUrl` only when a poster exists —
 * inventing any of them would describe a video that is not the one on the page. The caller guards
 * the null case: `Hero.tsx` only builds this when `launchFilm()` returned a film.
 */
export function videoObjectSchema(film: LaunchFilm, siteUrl: string): Record<string, unknown> {
  const node: Record<string, unknown> = {
    '@context': 'https://schema.org',
    '@type': 'VideoObject',
    name: film.title,
    contentUrl: absolute(film.sources[0].src, siteUrl),
    width: film.width,
    height: film.height,
  };
  if (film.description !== '') node.description = film.description;
  if (film.poster !== null) node.thumbnailUrl = absolute(film.poster, siteUrl);
  if (film.durationIso !== null) node.duration = film.durationIso;
  if (film.uploadDate !== null) node.uploadDate = film.uploadDate;
  return node;
}
