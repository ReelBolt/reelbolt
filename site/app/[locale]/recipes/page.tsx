import { RecipesPage, metadataFor } from '@/components/pages/RecipesPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <RecipesPage locale={await resolveLocale(params)} />;
}
