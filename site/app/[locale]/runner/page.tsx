import { RunnerPage, metadataFor } from '@/components/pages/RunnerPage';
import { resolveLocale, type LocaleParams } from '@/i18n/route';

export async function generateMetadata({ params }: LocaleParams) {
  return metadataFor(await resolveLocale(params));
}

export default async function Page({ params }: LocaleParams) {
  return <RunnerPage locale={await resolveLocale(params)} />;
}
