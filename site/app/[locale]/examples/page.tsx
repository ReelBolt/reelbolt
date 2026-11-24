import { ExamplesPage, metadataFor } from '@/components/pages/ExamplesPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <ExamplesPage locale={await resolveLocale(params)} />;
}
