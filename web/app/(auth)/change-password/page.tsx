import { Card, Title, Text, Stack } from '@mantine/core';
import { ChangePasswordForm } from '@/components/auth/ChangePasswordForm';
import { singleParam } from '@/lib/auth/config';
import { safeNextRedirect } from '@/lib/auth/redirect';
import { useT } from '@/i18n';

function ChangePasswordHeading() {
  const t = useT('auth.pages.changePassword');
  return (
    <Stack gap="md" mb="md">
      <Title order={2} ta="center">
        {t('title')}
      </Title>
      <Text size="sm" ta="center" c="dimmed">
        {t('subtitle')}
      </Text>
    </Stack>
  );
}

export default async function ChangePasswordPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  // A mustChangePassword session is sent here with ?next=<the consent URL it was heading for>
  // (api/handlers/oauth/oauth.go:132). The value is untrusted (it is in a URL a user can be
  // tricked into clicking), so it is validated as a same-origin, absolute path before it reaches
  // the form; anything else is dropped and the form falls back to the dashboard.
  const next = safeNextRedirect(singleParam(params.next));

  return (
    <Card radius="md" p="xl" withBorder>
      <ChangePasswordHeading />
      <ChangePasswordForm next={next ?? undefined} />
    </Card>
  );
}
