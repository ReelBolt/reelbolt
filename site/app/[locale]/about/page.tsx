import { AboutPage, metadataFor } from '@/components/pages/AboutPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <AboutPage locale={await resolveLocale(params)} />;
}
