import { notFound } from 'next/navigation';
import { TagDetailView } from '@/components/tags/TagDetailView';

/** Tag ids are GUIDs; anything else never reaches the API URL builders. */
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export default async function TagDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!GUID.test(id)) notFound();
  return <TagDetailView tagId={id} />;
}
