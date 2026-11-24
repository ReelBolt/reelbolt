import { redirect } from 'next/navigation';
import Link from 'next/link';
import { Container, Stack, Text, Title } from '@mantine/core';
import { videoEditorEnabled } from '@/lib/editor/config';
import { useT } from '@/i18n';

// The flag is read from the environment per request, so this page must not be prerendered at build
// time (see lib/editor/config.ts).
export const dynamic = 'force-dynamic';

/**
 * `/app/editor-coming-soon`: where `web/proxy.ts` sends every video-editor path while the flag is
 * off. It sits under `app/(app)/`, so it renders inside the authenticated shell like every other
 * app page — a visitor who is not signed in is still bounced to /login by the middleware first.
 *
 * With the flag ON the video editor is live and this route has no reason to exist, so it redirects
 * to the hub rather than staying reachable as a second, contradictory way in.
 *
 * This is a Server Component, so it uses `next/link` directly: passing a component (or any
 * function) as a prop to a Client Component — Mantine's `Button component={Link}` — is refused at
 * the RSC boundary and 500s the page.
 */
export default function EditorComingSoonPage() {
  const t = useT('misc.editorSoon');
  if (videoEditorEnabled()) redirect('/editor');

  return (
    <Container size="sm" py="xl">
      <Stack gap="sm" align="flex-start">
        <Title order={2}>{t('title')}</Title>
        <Text c="dimmed">{t('body')}</Text>
        <Link href="/projects" style={{ color: 'var(--mantine-color-anchor)' }}>{t('back')}</Link>
      </Stack>
    </Container>
  );
}
