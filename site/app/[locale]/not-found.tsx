import { NotFoundPage, metadataFor } from '@/components/pages/NotFoundPage';
import { getCurrentLocale } from '@/i18n/server';

export async function generateMetadata() {
  return metadataFor(await getCurrentLocale());
}

export default async function NotFound() {
  return <NotFoundPage locale={await getCurrentLocale()} />;
}
