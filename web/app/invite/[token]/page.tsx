'use client';

import { use, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { Alert, Badge, Button, Card, Center, Container, Group, Loader, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { BrandMark } from '@/components/shell/BrandMark';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAcceptInvite, useInvitationPreview } from '@/lib/hooks/use-orgs';
import { formatDate } from '@/lib/utils/format';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * Team invitation link (A16-UI; the API is C8's, api/handlers/auth/invitations.go).
 *
 * The link the API emails is `{PUBLIC_BASE_URL}/app/invite/{token}`, and the token in the path is
 * the credential — so this page is public and reads the preview before anyone signs in:
 *   - unknown, revoked or already-used token → 404, expired → 410, both explained plainly.
 *   - signed in as the invited address → "Join" accepts; the API re-issues the session for the
 *     joined workspace (cookies included), so the client only refreshes its auth snapshot.
 *   - signed in as someone else → the API would answer 403 ("this invitation was sent to a
 *     different email address"), so the page says which address it was sent to instead.
 *   - signed out → what the invite is for, plus a way in. Reaching the invited address is the
 *     visitor's own job: the email is prefilled by the signup page (C7-UI) when it ships, and
 *     `accountExists` decides whether that page offers signup or sign-in.
 */
function InviteView({ token }: { token: string }) {
  const router = useRouter();
  const t = useT('misc.invite');
  const tAll = useT();
  const { user, logout } = useAuth();
  const { data: invite, error, isLoading, mutate } = useInvitationPreview(token);
  const acceptInvite = useAcceptInvite();
  const [accepting, setAccepting] = useState(false);

  const status = error instanceof ApiError ? error.status : null;
  const invitedEmail = invite?.email ?? '';
  const signedInAs = user?.email ?? null;
  const matches = !!signedInAs && !!invitedEmail && signedInAs.toLowerCase() === invitedEmail.toLowerCase();

  async function handleAccept() {
    setAccepting(true);
    try {
      const session = await acceptInvite(token);
      notifications.show({
        title: t('welcomeTitle'),
        message: t('joined', { org: session.orgName, role: t('role', { role: session.orgRole }) }),
        color: 'green',
      });
      router.push('/dashboard');
    } catch (err: unknown) {
      notifications.show({
        title: t('joinFailedTitle'),
        message: describeError(tAll, err, t('joinFailedBody')),
        color: 'red',
      });
    } finally {
      setAccepting(false);
    }
  }

  if (isLoading) {
    return (
      <Card withBorder padding="xl">
        <Stack align="center" gap="sm">
          <Loader size="sm" />
          <Text size="sm" c="dimmed">
            {t('checking')}
          </Text>
        </Stack>
      </Card>
    );
  }

  if (error || !invite) {
    const expired = status === 410;
    const gone = status === 404;
    return (
      <Card withBorder padding="xl">
        <Stack gap="sm">
          <Title order={3}>
            {expired ? t('expiredTitle') : gone ? t('goneTitle') : t('errorTitle')}
          </Title>
          <Text size="sm" c="dimmed">
            {expired
              ? t('expiredBody')
              : gone
                ? t('goneBody')
                : describeError(tAll, error, t('errorBody'))}
          </Text>
          {!expired && !gone && (
            <Group>
              <Button variant="default" onClick={() => void mutate()}>
                {t('retry')}
              </Button>
            </Group>
          )}
          <Text size="sm">
            <Link href="/login">{t('goToSignIn')}</Link>
          </Text>
        </Stack>
      </Card>
    );
  }

  return (
    <Card withBorder padding="xl">
      <Stack gap="sm">
        <BrandMark size={40} />
        <Group gap="xs" wrap="nowrap">
          <Title order={3}>{invite.organizationName}</Title>
          <Badge variant="light">{t('role', { role: invite.role })}</Badge>
        </Group>
        <Text size="sm">
          {t.rich('summary', {
            hasInviter: invite.inviterName ? 'yes' : 'no',
            inviter: invite.inviterName ?? '',
            email: invitedEmail,
            org: invite.organizationName,
            role: t('role', { role: invite.role }),
            b: (chunks) => <b>{chunks}</b>,
          })}
        </Text>
        <Text size="xs" c="dimmed">
          {t('openUntil', { date: formatDate(invite.expiresAt) })}
        </Text>

        {signedInAs ? (
          matches ? (
            <Button onClick={handleAccept} loading={accepting} mt="sm">
              {t('join', { org: invite.organizationName })}
            </Button>
          ) : (
            <>
              <Alert icon={<IconInfoCircle size={16} />} color="yellow" variant="light">
                {t('wrongAccount', { invited: invitedEmail, current: signedInAs })}
              </Alert>
              <Group>
                <Button variant="default" onClick={() => void logout()}>
                  {t('signOut')}
                </Button>
              </Group>
            </>
          )
        ) : (
          <>
            <Alert icon={<IconInfoCircle size={16} />} color="blue" variant="light">
              {invite.accountExists
                ? t('signInAs', { email: invitedEmail })
                : t('needAccount', { email: invitedEmail })}
            </Alert>
            <Group>
              <Button component={Link} href="/login">
                {invite.accountExists ? t('signIn') : t('haveAccount')}
              </Button>
            </Group>
          </>
        )}
      </Stack>
    </Card>
  );
}

export default function InvitePage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = use(params);
  return (
    <Center mih="100vh" p="md">
      <Container size={480} w="100%">
        <InviteView token={token} />
      </Container>
    </Center>
  );
}
