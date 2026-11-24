import { ContactThankYouPage, metadataFor } from '@/components/pages/ContactThankYouPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <ContactThankYouPage locale={await resolveLocale(params)} />;
}
