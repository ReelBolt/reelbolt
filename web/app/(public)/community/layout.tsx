import type { Metadata } from 'next';
import { Box, Container } from '@mantine/core';
import { CommunityHeader } from '@/components/marketplace/CommunityHeader';
import { LanguageSelect } from '@/components/shared/LanguageSelect';
import { authPageConfig } from '@/lib/auth/config';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';

// SIGNUP_ENABLED is read from the environment per request (see lib/auth/config.ts), so this frame is not prerendered.
export const dynamic = 'force-dynamic';

// SEO for recipes is the marketing site's job: these pages stay out of search engines.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('marketplace.gallery');
  return { title: routeTitle(t('title')), robots: { index: false, follow: false } };
}

/** The signed-out frame of the community gallery: a minimal header, the page, and the language switch. */
export default function CommunityLayout({ children }: { children: React.ReactNode }) {
  const { signupEnabled } = authPageConfig();
  return (
    <Container size="xl" py="md" className="touch-targets">
      <CommunityHeader signupEnabled={signupEnabled} />
      <Box component="main" py="lg">
        {children}
      </Box>
      <Box maw={200}>
        <LanguageSelect withLabel={false} />
      </Box>
    </Container>
  );
}
