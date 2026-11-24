import { NotFoundPage, metadataFor } from '@/components/pages/NotFoundPage';
import { RootShell } from '@/components/layout/RootShell';
import { rootMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';

// An URL that matches no route at all (the app has two root layouts, so there is no single layout
// to compose a 404 from). Always English: a path outside every language has no locale to read.
export async function generateMetadata() {
  return { ...rootMetadata('en', { description: siteConfig.shortDescription }), ...(await metadataFor('en')) };
}

export default function GlobalNotFound() {
  return (
    <RootShell locale="en">
      <NotFoundPage locale="en" />
    </RootShell>
  );
}
