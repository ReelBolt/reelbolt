'use client';

import { usePathname } from 'next/navigation';
import { splitLocalePath } from './config';

/** The current URL path with any language prefix removed (`/it/pricing` -> `/pricing`), for "is this page X" checks. */
export function useBasePath(): string {
  const pathname = usePathname() ?? '/';
  const clean = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return splitLocalePath(clean).path;
}
