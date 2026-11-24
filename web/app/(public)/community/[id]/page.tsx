import { RecipeDetailView } from '@/components/marketplace/RecipeDetailView';

export default async function CommunityRecipePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <RecipeDetailView id={id} />;
}
