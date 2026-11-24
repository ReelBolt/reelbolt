import { redirect } from 'next/navigation';

interface Props {
  params: Promise<{
    id: string;
    stepResultId: string;
  }>;
}

export default async function EditorRedirectPage({ params }: Props) {
  const { id, stepResultId } = await params;
  redirect(`/projects/${id}/editor/${stepResultId}`);
}
