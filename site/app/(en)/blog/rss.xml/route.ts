import { listedPosts } from '@/lib/blog/posts';
import { buildRss } from '@/lib/blog/rss';
import { siteConfig } from '@/lib/site-config';

// Generated once at build time for the static export; the same XML is served by the Node build.
export const dynamic = 'force-static';

export function GET() {
  return new Response(buildRss(listedPosts(), siteConfig.url), {
    headers: { 'Content-Type': 'application/rss+xml; charset=utf-8' },
  });
}
