import { redirect } from 'next/navigation';
import { editorHubHref } from '@/lib/editing/open-timeline';

/** The media workspace now lives in the editor hub's Media view; old links land there. */
export default async function MediaWorkspaceRedirect({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  redirect(editorHubHref(id, 'media'));
}
