import type { Metadata } from 'next';
import { AdminMarketplace } from '@/components/marketplace/AdminMarketplace';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('marketplace.admin');
  return { title: routeTitle(t('title')) };
}

export default function AdminMarketplacePage() {
  return <AdminMarketplace />;
}
