import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';

// This route's tab title. It is here rather than in the page because the page is a client
// component and Next reads `metadata` from Server Components only; see lib/metadata.ts.
export const metadata: Metadata = { title: routeTitle('Thumbnail editor') };

export default function ThumbnailEditorLayout({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
