import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

// This route's tab title (see settings/usage/layout.tsx for why it lives in a layout).
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('settings');
  return { title: routeTitle(t('meta.data')) };
}

export default function DataSettingsLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
