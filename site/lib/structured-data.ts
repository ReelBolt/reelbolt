import { siteConfig } from '@/lib/site-config';
import { currency, plans } from '@/lib/pricing';
import { aggregateOfferNode, annualPlanOffers, pricedPlanOffers } from '@/lib/pricing/offers';
import type { Faq } from '@/lib/faqs';
import type { BlogPost } from '@/lib/blog/types';
import { defaultLocale, localeMeta, localizePath, type Locale } from '@/i18n/config';
import { seoStrings } from '@/lib/seo-strings';
import { productImageUrl } from '@/lib/product-image';

/** Absolute URL of a path in the given language (untranslated paths keep their one URL). */
function pageUrl(locale: Locale, path = '/'): string {
  const localized = localizePath(locale, path);
  // The bare origin for a home page, as before: no trailing slash.
  return localized === '/' ? siteConfig.url : new URL(localized, siteConfig.url).toString();
}

// Deliberately no `contactPoint`/`address` here — those need real legal facts that don't exist
// until `legal-placeholders.ts` is filled in. Extend this file once that data is real rather than
// inventing placeholder values now. `aggregateRating` stays banned for the same reason: there are
// no real reviews, and fabricated review markup is a manual-action-level violation, not a growth
// hack.
//
// `offers` ARE emitted now — there are real, published prices (decision D24), so the homepage
// carries an AggregateOffer and /pricing carries one Offer per priced plan. The rule that replaces
// the old ban is a single-source rule: **every price comes from `site/lib/pricing`**, the vendored
// copy of the billing catalog. No price is ever hand-written in markup or in copy — the numbers in
// these schemas and the numbers a reader sees are generated from the same file, so they cannot
// disagree. A `contact` plan (Enterprise) publishes no price and gets no Offer at all.
export function organizationSchema(locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    '@type': 'Organization',
    name: siteConfig.name,
    url: siteConfig.url,
    logo: `${siteConfig.url}/icon.svg`,
    description: seoStrings(locale).description,
  };
}

export function websiteSchema(locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    '@type': 'WebSite',
    name: siteConfig.name,
    url: pageUrl(locale),
    description: seoStrings(locale).description,
    inLanguage: localeMeta(locale).htmlLang,
  };
}

export function softwareApplicationSchema(locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    '@type': 'SoftwareApplication',
    name: siteConfig.name,
    url: pageUrl(locale),
    image: productImageUrl(siteConfig.url),
    inLanguage: localeMeta(locale).htmlLang,
    applicationCategory: 'MultimediaApplication',
    applicationSubCategory: 'Video Production Software',
    // Deliberately not grown yet: the desktop runner's signed installers are not released (E10b),
    // so the only platforms a customer can actually use are a browser and, on the self-hosted
    // option, the host's own OS. Claiming runner platforms before the download exists would be an
    // invented fact; the value grows in the same commit as `lib/runner-downloads` gains artifacts.
    operatingSystem: 'Web-based',
    description: seoStrings(locale).description,
    // The range across the recurring plans. Recomputed from the catalog on every render, so an
    // edited price moves the page and the markup together (see `lib/pricing/offers`).
    offers: aggregateOfferNode(plans, pricedPlanOffers(plans, currency, siteConfig.url), currency),
    featureList: seoStrings(locale).featureList,
  };
}

/**
 * The Offer markup for /pricing: one Offer per priced plan, each pointing at the card anchor that
 * shows that price. Enterprise publishes no price, so it is omitted rather than guessed at
 * (decision D7 keeps it "Contact us" only).
 */
export function pricingOffersSchema(locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    // An OfferCatalog, not a Product: Google reads Product markup with offers as a merchant
    // product snippet and asks for `review` and `aggregateRating`, which this site does not have
    // and will not invent. The prices stay published, and a catalog is not a rich-result type.
    '@type': 'OfferCatalog',
    name: seoStrings(locale).plansName.replace('{name}', siteConfig.name),
    description: seoStrings(locale).description,
    url: pageUrl(locale, '/pricing'),
    inLanguage: localeMeta(locale).htmlLang,
    itemListElement: [...pricedPlanOffers(plans, currency, siteConfig.url), ...annualPlanOffers(plans, currency, siteConfig.url)],
  };
}

export function faqSchema(faqs: Faq[], locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    '@type': 'FAQPage',
    inLanguage: localeMeta(locale).htmlLang,
    mainEntity: faqs.map((faq) => ({
      '@type': 'Question',
      name: faq.question,
      acceptedAnswer: {
        '@type': 'Answer',
        text: faq.answer,
      },
    })),
  };
}

// `position` is 1-based and must be contiguous, so the caller passes the trail in display order
// and never has to hand-maintain the indices.
export function breadcrumbSchema(trail: { name: string; path: string }[], locale: Locale = defaultLocale) {
  return {
    '@context': 'https://schema.org',
    '@type': 'BreadcrumbList',
    itemListElement: trail.map((crumb, index) => ({
      '@type': 'ListItem',
      position: index + 1,
      name: crumb.name,
      item: pageUrl(locale, crumb.path),
    })),
  };
}

/**
 * Article markup for one blog post. `publisher` and `author` are the same Organization node the
 * root layout emits, so an article can never name a different publisher than the site. `image` is
 * the post's generated OG image, the one a social card shows.
 */
export function articleSchema(post: Pick<BlogPost, 'slug' | 'title' | 'description' | 'date' | 'updated'>) {
  const url = new URL(`/blog/${post.slug}`, siteConfig.url).toString();
  return {
    '@context': 'https://schema.org',
    '@type': 'Article',
    headline: post.title,
    description: post.description,
    datePublished: post.date,
    dateModified: post.updated ?? post.date,
    author: organizationSchema(),
    publisher: organizationSchema(),
    image: new URL(`/blog/${post.slug}/opengraph-image`, siteConfig.url).toString(),
    mainEntityOfPage: { '@type': 'WebPage', '@id': url },
  };
}
