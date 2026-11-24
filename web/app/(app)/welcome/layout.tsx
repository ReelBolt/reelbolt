import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

// The tab title lives here because Next reads `metadata` from Server Components only; see
// lib/metadata.ts for why the title is an object rather than a bare string.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('welcome.meta');
  return { title: routeTitle(t('title')) };
}

export default function WelcomeLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
