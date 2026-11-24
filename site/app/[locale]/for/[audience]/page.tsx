import { AudiencePage, audienceLocaleStaticParams, metadataFor } from '@/components/pages/AudiencePage';
import { resolveLocale } from '@/i18n/route';

type Params = { params: Promise<{ locale: string; audience: string }> };

// The six audience pages of `lib/audiences/registry`, once per language. Any other slug is a 404.
export const dynamicParams = false;
export const generateStaticParams = audienceLocaleStaticParams;

export async function generateMetadata({ params }: Params) {
  const { audience } = await params;
  return metadataFor(await resolveLocale(params), audience);
}

export default async function Page({ params }: Params) {
  const { audience } = await params;
  return <AudiencePage locale={await resolveLocale(params)} slug={audience} />;
}
