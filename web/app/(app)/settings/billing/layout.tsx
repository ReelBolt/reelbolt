import type { Metadata } from 'next';
import { getT } from '@/i18n/server';
import { routeTitle } from '@/lib/metadata';

// This route's tab title. It is here rather than in the page because the page is a client
// component and Next reads `metadata` from Server Components only; see lib/metadata.ts for why
// the title is an object rather than a bare string.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('billing.page');
  return { title: routeTitle(t('metaTitle')) };
}

export default function PlanAndBillingLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
