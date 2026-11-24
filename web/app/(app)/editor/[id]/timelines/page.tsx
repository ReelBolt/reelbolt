import { redirect } from 'next/navigation';
import { editorHubHref } from '@/lib/editing/open-timeline';

/** The project's timelines now live in the editor hub's Timelines view; old links land there. */
export default async function ProjectTimelinesRedirect({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  redirect(editorHubHref(id, 'timelines'));
}
