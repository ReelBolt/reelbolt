import type { Metadata } from 'next';
import { MyRecipes } from '@/components/marketplace/MyRecipes';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('marketplace.mine');
  return { title: routeTitle(t('title')) };
}

export default function MyRecipesPage() {
  return <MyRecipes />;
}
