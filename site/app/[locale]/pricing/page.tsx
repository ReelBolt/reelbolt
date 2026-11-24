import { PricingPage, metadataFor } from '@/components/pages/PricingPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <PricingPage locale={await resolveLocale(params)} />;
}
