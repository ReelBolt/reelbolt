import { RootShell } from '@/components/layout/RootShell';
import { rootMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';

// English, unprefixed. The root layout of the default language; `app/[locale]/layout.tsx` is the
// other root layout, for every prefixed language. Both render the same shell.
export const metadata = rootMetadata('en', { description: siteConfig.shortDescription });

export default function EnglishRootLayout({ children }: { children: React.ReactNode }) {
  return <RootShell locale="en">{children}</RootShell>;
}
