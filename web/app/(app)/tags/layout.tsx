import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('tags.meta');
  return { title: routeTitle(t('tags')) };
}

export default function TagsLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
