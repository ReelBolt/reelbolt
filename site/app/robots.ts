import type { MetadataRoute } from 'next';
import { siteConfig } from '@/lib/site-config';
import { prefixedLocales } from '@/i18n/config';

export const dynamic = 'force-static';

const DISALLOW = ['/api/', '/contact/thank-you', ...prefixedLocales.map((l) => `/${l}/contact/thank-you`)];

// AI crawlers named explicitly, each with the same allow/disallow as `*`, so a policy change to
// the shared rule reaches them too. Bytespider is deliberately not listed. `/llms.txt` is not a
// robots directive, so there is no line for it here; it is discovered by URL, not by this file.
const AI_CRAWLERS = [
  'GPTBot',
  'OAI-SearchBot',
  'ChatGPT-User',
  'ClaudeBot',
  'Claude-User',
  'Claude-SearchBot',
  'anthropic-ai',
  'PerplexityBot',
  'Perplexity-User',
  'Google-Extended',
  'Applebot-Extended',
  'CCBot',
];

// Project Arachnid Shield's verification crawler. The token is published here by design (it is
// read from robots.txt); it proves we authorised Shield to scan our services. Allow everything.
const PROJECT_ARACHNID_USER_AGENT = 'ProjectArachnid/SKFM2QXEFI6MV3LOEBYQDVRYUMCZLNCP';

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      { userAgent: '*', allow: '/', disallow: DISALLOW },
      { userAgent: PROJECT_ARACHNID_USER_AGENT, allow: '/' },
      ...AI_CRAWLERS.map((userAgent) => ({ userAgent, allow: '/', disallow: DISALLOW })),
    ],
    // One sitemap line for the whole file, however many groups it has.
    sitemap: `${siteConfig.url}/sitemap.xml`,
    host: siteConfig.url,
  };
}
