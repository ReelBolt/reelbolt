import NextLink from 'next/link';
import type { ComponentProps } from 'react';
import { CarryLink } from '@/components/analytics/CarryLink';
import { leavesLocaleLayout, localizePath } from './config';
import { useCurrentLocale } from './index';

/**
 * `next/link` that keeps the visitor in their language on translated pages (legal, blog and /app
 * links pass through).
 *
 * A link from a translated page to a page that exists only in English crosses from one root layout
 * to the other, which the browser does as a full document load. Such a link is rendered through
 * `CarryLink`, which adds the visitor's audience and campaign parameters to the URL in the browser,
 * so the next document knows the same landing (see `useCarryHref`). The server-rendered href is the
 * plain one either way.
 */
export function Link({ href, ...props }: ComponentProps<typeof NextLink>) {
  const locale = useCurrentLocale();
  if (typeof href === 'string' && leavesLocaleLayout(locale, href)) return <CarryLink href={href} {...props} />;
  return <NextLink href={typeof href === 'string' ? localizePath(locale, href) : href} {...props} />;
}

/** Same rule for a plain `<a>` or a programmatic push. */
export function useLocalizedHref(): (href: string) => string {
  const locale = useCurrentLocale();
  return (href) => localizePath(locale, href);
}
