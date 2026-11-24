import { Alert, Card, Center, Title, Text, Stack } from '@mantine/core';
import { headers } from 'next/headers';
import { IconAlertCircle, IconCalendarEvent } from '@tabler/icons-react';
import { LoginForm } from '@/components/auth/LoginForm';
import { BrandMark } from '@/components/shell/BrandMark';
import { authPageConfig, singleParam } from '@/lib/auth/config';
import { offeredOAuth } from '@/lib/auth/oauth-server';
import { oauthErrorNotice } from '@/lib/auth/oauth';
import { isInAppBrowser } from '@/lib/auth/in-app-browser';
import { safeNextRedirect } from '@/lib/auth/redirect';
import { readSignupCarry } from '@/lib/auth/signup-carry';
import { SESSION_ENDED_MESSAGE, SESSION_EXPIRED_REASON } from '@/lib/auth/session-expiry';
import { DELETION_GRACE_DAYS, formatDeletionDate } from '@/lib/account/format';
import { useT } from '@/i18n';

/** Product name: a proper noun, identical in every language. */
const BRAND_NAME = 'ReelBolt';

// SIGNUP_ENABLED, the OAuth flags and the Turnstile site key are read from the environment per
// request, so this page must not be prerendered at build time (see lib/auth/config.ts).
export const dynamic = 'force-dynamic';

function LoginSubtitle() {
  const t = useT('auth.pages.login');
  return (
    <Text size="sm" ta="center" c="dimmed">
      {t('subtitle')}
    </Text>
  );
}

function DeletionScheduledNotice({ deletionDate }: { deletionDate: string | null }) {
  const t = useT('auth.pages.login');
  return (
    <Alert
      icon={<IconCalendarEvent size={16} />}
      color="violet"
      variant="light"
      mb="md"
      role="status"
      title={t('deletionTitle')}
      data-testid="deletion-scheduled-notice"
    >
      {t('deletionBody', {
        hasDate: deletionDate ? 'yes' : 'no',
        date: deletionDate ?? '',
        days: DELETION_GRACE_DAYS,
      })}
    </Alert>
  );
}

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const config = authPageConfig();
  // The buttons the API can actually serve; the env flags only when it cannot be asked.
  const oauth = await offeredOAuth();
  // The OAuth callback sends a browser back here with a reason code rather than a JSON body.
  const oauthError = singleParam(params.error);
  // An OAuth authorize hit on a signed-out user lands here with ?next=<the consent URL it was trying
  // to reach>. The value is untrusted (it is in a URL a user can be tricked into clicking), so it is
  // validated as a same-origin, absolute path before it is passed on; anything else is dropped.
  const next = safeNextRedirect(singleParam(params.next));
  // Everything the visitor's own link carried (`for`, `plan`, `utm_*` and the same validated
  // `next`), sanitized. The form keeps it on the "Create account" link and on the provider buttons.
  const carry = readSignupCarry(params);
  // Inside another app's built-in browser Google refuses to sign anyone in: its button gives way
  // to a sentence about opening the page in the real browser (lib/auth/in-app-browser.ts).
  const inAppBrowser = isInAppBrowser((await headers()).get('user-agent'));
  // Where a deletion request lands once the session is gone: from the account page (with the date)
  // and from a Google/GitHub confirmation (with the date since it was added, maybe without on an
  // older server). The date is only ever displayed, after it has parsed as one.
  const sessionEnded = singleParam(params.reason) === SESSION_EXPIRED_REASON;
  const deletionScheduled = singleParam(params.deletion) === 'scheduled';
  const deletionDate = formatDeletionDate(singleParam(params.date));

  return (
    <Card radius="md" p="xl" withBorder>
      <Stack gap="md" mb="md">
        <Center>
          <BrandMark size={56} />
        </Center>
        <Title order={1} size="h2" ta="center">
          {BRAND_NAME}
        </Title>
        <LoginSubtitle />
      </Stack>
      {sessionEnded && (
        <Alert
          icon={<IconAlertCircle size={16} />}
          color="blue"
          variant="light"
          mb="md"
          role="status"
          data-testid="session-ended-notice"
        >
          {SESSION_ENDED_MESSAGE}
        </Alert>
      )}
      {deletionScheduled && <DeletionScheduledNotice deletionDate={deletionDate} />}
      {oauthError && (
        <Alert
          icon={<IconAlertCircle size={16} />}
          color="red"
          variant="light"
          mb="md"
          role="alert"
        >
          {oauthErrorNotice(oauthError)}
        </Alert>
      )}
      <LoginForm
        signupEnabled={config.signupEnabled}
        oauthProviders={oauth?.providers ?? config.oauthProviders}
        next={next ?? undefined}
        carry={carry}
        inAppBrowser={inAppBrowser}
      />
    </Card>
  );
}
