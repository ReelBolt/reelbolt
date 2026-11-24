import { Card, Stack, Text, Title } from '@mantine/core';
import type { Metadata } from 'next';
import { VerifyEmailStatus } from '@/components/auth/VerifyEmailStatus';
import { authPageConfig, singleParam } from '@/lib/auth/config';
import { safeNextRedirect } from '@/lib/auth/redirect';
import { getT } from '@/i18n/server';

// The token travels in the query string, so it must never ride along in a Referer header sent to
// any other origin (docs/design/saas-accounts.md, "Token rules").
export const metadata: Metadata = { referrer: 'no-referrer' };

// The Turnstile site key is read from the environment per request (lib/auth/config.ts).
export const dynamic = 'force-dynamic';

export default async function VerifyEmailPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const token = singleParam(params.token);
  // Untrusted, like the sign-in page's: only a same-origin absolute path survives.
  const next = safeNextRedirect(singleParam(params.next));
  const t = await getT('auth.pages.verify');

  return (
    <Card radius="md" p="xl" withBorder>
      <Stack gap="md" mb="md">
        <Title order={2} ta="center">
          {t('title')}
        </Title>
        <Text size="sm" ta="center" c="dimmed">
          {t('subtitle')}
        </Text>
      </Stack>
      <VerifyEmailStatus token={token} next={next ?? undefined} turnstileSiteKey={authPageConfig().turnstileSiteKey} />
    </Card>
  );
}
