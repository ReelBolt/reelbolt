import { ContactPage, metadataFor } from '@/components/pages/ContactPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <ContactPage locale={await resolveLocale(params)} />;
}
