import type { Metadata } from 'next';
import { siteConfig } from '@/lib/site-config';
import {
  alternateLanguages,
  defaultLocale,
  isTranslatedPath,
  localeList,
  localeMeta,
  localizePath,
  type Locale,
} from '@/i18n/config';

export interface SeoImage {
  /** Absolute, or a path resolved against `siteConfig.url`. */
  url: string;
  width: number;
  height: number;
  alt: string;
}

export function buildMetadata({
  title,
  description,
  path,
  noIndex,
  type = 'website',
  publishedTime,
  modifiedTime,
  tags,
  image,
  locale = defaultLocale,
}: {
  title: string;
  description: string;
  path: string;
  noIndex?: boolean;
  /** `article` adds the Open Graph article fields. Defaults to `website`, as before. */
  type?: 'website' | 'article';
  publishedTime?: string;
  modifiedTime?: string;
  tags?: string[];
  image?: SeoImage;
  /** The page's language. A translated path then gets its own canonical and hreflang alternates. */
  locale?: Locale;
}): Metadata {
  const translated = isTranslatedPath(path);
  const url = absoluteUrl(translated ? localizePath(locale, path) : path);
  // Pages without their own art share the site card, the same image `app/opengraph-image` serves.
  const defaultCard = locale === defaultLocale ? '/opengraph-image' : `/${locale}/opengraph-image`;
  const card: SeoImage = image ?? { url: defaultCard, width: 1200, height: 630, alt: siteConfig.name };
  const imageUrl = new URL(card.url, siteConfig.url).toString();
  const images = [{ url: imageUrl, width: card.width, height: card.height, alt: card.alt }];

  // `title.absolute` bypasses ancestor `title.template` merging entirely. We need that here
  // rather than passing `title` as a plain string, because Next.js does not apply a layout's
  // title.template to a page.tsx that shares the same route segment as that layout — which is
  // exactly the case for the root `app/page.tsx` (verified against the built output: nested
  // routes like /about get the " | ReelBolt" suffix from the template automatically, but the
  // root page never does). Composing the full title explicitly here keeps every page's <title>
  // consistent regardless of route depth, instead of splitting the behavior between
  // template-inheritance (nested routes) and manual concatenation (root route).
  const ogLocale = localeMeta(locale).ogLocale;
  const alternateOg = localeList.filter((l) => l.code !== locale).map((l) => l.ogLocale);
  const fullTitle = `${title} | ${siteConfig.name}`;

  const metadata: Metadata = {
    title: { absolute: fullTitle },
    description,
    alternates: {
      canonical: url,
      // Only pages that exist in every language: legal and blog are English-only and carry none.
      ...(translated ? { languages: hreflangMap(path) } : {}),
    },
    openGraph:
      type === 'article'
        ? {
            type: 'article',
            url,
            siteName: siteConfig.name,
            title,
            description,
            locale: ogLocale,
            publishedTime,
            modifiedTime,
            tags,
            images,
          }
        : {
            type: 'website',
            url,
            siteName: siteConfig.name,
            title,
            description,
            locale: ogLocale,
            ...(translated && alternateOg.length ? { alternateLocale: alternateOg } : {}),
            images,
          },
    twitter: {
      card: 'summary_large_image',
      title,
      description,
      images: [imageUrl],
    },
  };

  if (noIndex) {
    metadata.robots = { index: false, follow: false };
  }

  return metadata;
}

function absoluteUrl(path: string): string {
  return new URL(path, siteConfig.url).toString();
}

/** hreflang -> absolute URL, with x-default pointing at the English page. */
export function hreflangMap(path: string): Record<string, string> {
  return Object.fromEntries(Object.entries(alternateLanguages(path)).map(([lang, p]) => [lang, absoluteUrl(p)]));
}

/** Site-wide defaults the root layout of each language supplies (title template, OG and Twitter fallbacks). */
export function rootMetadata(locale: Locale, strings: { description: string }): Metadata {
  const ogLocale = localeMeta(locale).ogLocale;
  return {
    metadataBase: new URL(siteConfig.url),
    title: { default: siteConfig.name, template: `%s | ${siteConfig.name}` },
    description: strings.description,
    openGraph: {
      type: 'website',
      url: absoluteUrl(localizePath(locale, '/')),
      siteName: siteConfig.name,
      title: siteConfig.name,
      description: strings.description,
      locale: ogLocale,
    },
    twitter: { card: 'summary_large_image', title: siteConfig.name, description: strings.description },
  };
}
