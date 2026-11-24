'use client';

import NextLink from 'next/link';
import type { ComponentProps } from 'react';
import { useCarryHref } from './useSignupHref';

/**
 * `next/link` for a link the browser will open as a NEW document although it stays on this site:
 * a link from a translated page (`/it/…`) to an English-only one (the blog, the legal pages). The
 * two live under different root layouts, so the navigation is a full load and the in-memory
 * landing would be lost with it. The link therefore carries the audience, the campaign parameters
 * and (with a campaign) the path of the page the visit began on in its URL (`useCarryHref`);
 * nothing is stored.
 *
 * `@/i18n/link` chooses this component by itself. Do not use it for a link that stays inside one
 * layout.
 */
export function CarryLink({ href, ...props }: Omit<ComponentProps<typeof NextLink>, 'href'> & { href: string }) {
  return <NextLink href={useCarryHref(href)} {...props} />;
}
