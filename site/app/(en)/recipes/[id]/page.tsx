import { RecipePage, metadataFor, recipeStaticParams } from '@/components/pages/RecipePage';

type Params = { params: Promise<{ id: string }> };

export const generateStaticParams = recipeStaticParams;

export async function generateMetadata({ params }: Params) {
  return metadataFor('en', (await params).id);
}

export default async function Page({ params }: Params) {
  return <RecipePage locale="en" id={(await params).id} />;
}
