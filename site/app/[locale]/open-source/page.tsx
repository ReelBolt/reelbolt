import { OpenSourcePage, metadataFor } from '@/components/pages/OpenSourcePage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <OpenSourcePage locale={await resolveLocale(params)} />;
}
