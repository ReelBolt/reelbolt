import { redirect } from 'next/navigation';
import { editorHubHref } from '@/lib/editing/open-timeline';

/**
 * The old read-only viewer for a downloaded timeline file is gone: a timeline is opened, edited
 * and exchanged (OpenTimelineIO import/export) in the one editor now. Old links land on the hub.
 */
export default function OfflineTimelinePreviewRedirect() {
  redirect(editorHubHref());
}
