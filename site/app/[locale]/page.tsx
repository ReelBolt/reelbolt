import { HomePage, metadataFor } from '@/components/pages/HomePage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <HomePage locale={await resolveLocale(params)} />;
}
