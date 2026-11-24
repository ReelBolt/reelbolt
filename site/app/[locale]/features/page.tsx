import { FeaturesPage, metadataFor } from '@/components/pages/FeaturesPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <FeaturesPage locale={await resolveLocale(params)} />;
}
