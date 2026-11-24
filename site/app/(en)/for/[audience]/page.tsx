import { AudiencePage, audienceStaticParams, metadataFor } from '@/components/pages/AudiencePage';

type Params = { params: Promise<{ audience: string }> };

// The six audience pages of `lib/audiences/registry` and nothing else: any other slug is a 404.
export const dynamicParams = false;
export const generateStaticParams = audienceStaticParams;

export async function generateMetadata({ params }: Params) {
  return metadataFor('en', (await params).audience);
}

export default async function Page({ params }: Params) {
  return <AudiencePage locale="en" slug={(await params).audience} />;
}
