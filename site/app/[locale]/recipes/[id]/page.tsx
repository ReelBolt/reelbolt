import { RecipePage, metadataFor, recipeLocaleStaticParams } from '@/components/pages/RecipePage';
import { resolveLocale } from '@/i18n/route';

type Params = { params: Promise<{ locale: string; id: string }> };

export const generateStaticParams = recipeLocaleStaticParams;

export async function generateMetadata({ params }: Params) {
  const { id } = await params;
  return metadataFor(await resolveLocale(params), id);
}

export default async function Page({ params }: Params) {
  const { id } = await params;
  return <RecipePage locale={await resolveLocale(params)} id={id} />;
}
