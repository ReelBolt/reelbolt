import type { Metadata } from 'next';
import { RootShell } from '@/components/layout/RootShell';
import { localeStaticParams, resolveLocale, type LocaleParams } from '@/i18n/route';
import { rootMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';

// Every registered language except the default, from the registry: adding one needs no new route.
export const dynamicParams = false;
export const generateStaticParams = localeStaticParams;

export async function generateMetadata({ params }: LocaleParams): Promise<Metadata> {
  const locale = await resolveLocale(params);
  return rootMetadata(locale, { description: siteConfig.shortDescription });
}

export default async function LocaleRootLayout({
  children,
  params,
}: LocaleParams & { children: React.ReactNode }) {
  const locale = await resolveLocale(params);
  return <RootShell locale={locale}>{children}</RootShell>;
}
