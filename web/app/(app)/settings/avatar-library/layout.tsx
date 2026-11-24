import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

// This route's tab title. It is here rather than in the page because the page is a client
// component and Next reads `metadata` from Server Components only; see lib/metadata.ts for why
// the title is an object rather than a bare string.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('avatars.library.page');
  return { title: routeTitle(t('title')) };
}

export default function AvatarLibraryLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
