import type { Metadata, Viewport } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';
import { EditorSurface } from '@/components/editor/ui/EditorDisplay';

// This route's tab title. It is here rather than in the page because the page is a client
// component and Next reads `metadata` from Server Components only; see lib/metadata.ts for why
// the title is an object rather than a bare string.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('projects.meta');
  return { title: routeTitle(t('editor')) };
}

// VF17: the editor draws edge to edge, so the browser must report the notch and home-bar insets
// (env(safe-area-inset-*)); app/global.css keeps the bars, dock, sheets and toasts out from under them.
export const viewport: Viewport = { viewportFit: 'cover' };

export default function EditorLayout({ children }: { children: React.ReactNode }) {
  return <EditorSurface>{children}</EditorSurface>;
}
