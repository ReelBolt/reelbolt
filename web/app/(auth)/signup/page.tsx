import { Anchor, Card, Center, Divider, Stack, Text, Title } from '@mantine/core';
import { headers } from 'next/headers';
import { OAuthButtons } from '@/components/auth/OAuthButtons';
import { SignupForm } from '@/components/auth/SignupForm';
import { SignupValuePanel } from '@/components/auth/SignupValuePanel';
import { BrandMark } from '@/components/shell/BrandMark';
import { authPageConfig, singleParam } from '@/lib/auth/config';
import { offeredOAuth } from '@/lib/auth/oauth-server';
import { servedLegalVersions } from '@/lib/auth/legal-server';
import { isInAppBrowser } from '@/lib/auth/in-app-browser';
import { readSignupCarry } from '@/lib/auth/signup-carry';
import { welcomeFlowEnabled } from '@/lib/onboarding/config';
import { SignupTracker } from '@/components/analytics/SignupTracker';
import { useT } from '@/i18n';
import classes from './signup.module.css';

// SIGNUP_ENABLED and the Turnstile site key are read from the environment per request, so this
// page must not be prerendered at build time (see lib/auth/config.ts).
export const dynamic = 'force-dynamic';

/**
 * The card's heading. Someone starting on their own reads what they will get (the outcome), then
 * that it is free to start. An invited teammate, and a visitor to an instance that takes no
 * sign-ups, keep the plain "Create your account": the outcome line is not what they came for.
 */
function SignupHeading({ invite, open }: { invite: boolean; open: boolean }) {
  const t = useT('auth.pages.signup');
  const outcomeLed = open && !invite;
  return (
    <>
      <Title order={1} size="h2" ta="center">
        {outcomeLed ? t('title') : t('titlePlain')}
      </Title>
      {(invite || outcomeLed) && (
        <Text size="sm" ta="center" c="dimmed">
          {invite ? t('subtitleInvite') : t('subtitle')}
        </Text>
      )}
    </>
  );
}

function SignupOrDivider({ formFirst }: { formFirst: boolean }) {
  const t = useT('auth.pages.signup');
  return <Divider label={formFirst ? t('orProvider') : t('orEmail')} labelPosition="center" my="md" />;
}

function SignupClosed() {
  const t = useT('auth.pages.signup');
  return (
    <Stack gap="sm">
      <Text size="sm">{t('closed')}</Text>
      <Text size="sm" ta="center">
        {/*
          A plain anchor, deliberately NOT Mantine's `component={Link}`. This page is a
          Server Component (it reads SIGNUP_ENABLED per request), and `next/link` resolves to
          a plain function in the RSC graph — handing it to Mantine's `component` prop
          crosses the server/client boundary with a function and the render throws:
            "Functions cannot be passed directly to Client Components..."
          The consequence was a hard 500 on /app/signup for exactly this deployment, because
          the branch below renders only when self-serve sign-up is switched OFF.
          A full page load to the sign-in page costs nothing here, so the anchor stays plain —
          and, being raw, it must carry the dashboard's '/app' basePath itself: '/login' alone
          is the marketing site's (missing) page, not ours.
        */}
        <Anchor href="/app/login">{t('backToSignIn')}</Anchor>
      </Text>
    </Stack>
  );
}

export default async function SignupPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const params = await searchParams;
  const config = authPageConfig();
  // The buttons the API can actually serve; the env flags only when it cannot be asked.
  const oauth = await offeredOAuth();
  // The versions this deployment is serving, read from the service that owns them (F-7). A null
  // answer renders the form without a version; the API refuses a submission that names nothing it
  // serves and answers with the current ones, which the form then shows.
  const legal = await servedLegalVersions();
  // A provider button on the SIGNUP page creates an account, which the API allows only when signup
  // is open; on an invite-only server it would only ever answer "ask for an invite".
  const signupProviders = oauth
    ? oauth.createsAccount ? oauth.providers : []
    : config.signupEnabled ? config.oauthProviders : [];
  const initialEmail = singleParam(params.email);
  const inviteToken = singleParam(params.invite);
  // What the visitor's own link carried: the audience (`?for=`), the campaign parameters, the plan
  // and a return path. Read from the URL and sanitized here; nothing is stored on the device.
  const carry = readSignupCarry(params);
  // Inside another app's built-in browser (a link opened from Instagram, TikTok, LinkedIn...) Google
  // refuses to sign anyone in. There the email form leads, and the Google button gives way to a
  // sentence about opening the page in the real browser (lib/auth/in-app-browser.ts).
  const inAppBrowser = isInAppBrowser((await headers()).get('user-agent'));
  // Whether the two welcome questions follow sign-in on this deployment (WELCOME_FLOW_ENABLED): the
  // page only promises them when they exist.
  const welcomeEnabled = welcomeFlowEnabled();
  const providerButtons = signupProviders.length > 0 && (
    <OAuthButtons providers={signupProviders} carry={carry} signup inAppBrowser={inAppBrowser} />
  );

  const card = (
    <Card radius="md" p="xl" withBorder>
      <Stack gap="md" mb="md">
        <Center>
          <BrandMark size={56} />
        </Center>
        <SignupHeading invite={!!inviteToken} open={config.signupEnabled} />
      </Stack>
      {/* Provider sign-in comes first: one click and no password to choose. The form follows.
          Inside an in-app browser the order is reversed: the form is the way that works there. */}
      {providerButtons && !inAppBrowser && (
        <>
          {providerButtons}
          <SignupOrDivider formFirst={false} />
        </>
      )}
      {config.signupEnabled ? (
        <SignupForm
          turnstileSiteKey={config.turnstileSiteKey}
          termsVersion={legal?.termsVersion ?? ''}
          privacyVersion={legal?.privacyVersion ?? ''}
          initialEmail={initialEmail}
          inviteToken={inviteToken}
          carry={carry}
          welcomeEnabled={welcomeEnabled}
        />
      ) : (
        // Self-host keeps D12's invite-only default: the API answers 404 to /auth/signup, so the
        // page must not offer a form that could never succeed.
        <SignupClosed />
      )}
      {providerButtons && inAppBrowser && (
        <>
          <SignupOrDivider formFirst />
          {providerButtons}
        </>
      )}
    </Card>
  );

  // The value panel is for someone deciding whether to start: not for an invited teammate (whose
  // next steps are different: no email to confirm, a team to join) and not on an instance that
  // takes no sign-ups. Those keep the single card.
  const showValue = config.signupEnabled && !inviteToken;

  return (
    <>
      <SignupTracker />
      {showValue ? (
        <div className={classes.split}>
          {/* Document order: the form first (that is the phone layout), then the panel. */}
          {card}
          <div className={classes.value}>
            <SignupValuePanel persona={carry.persona} welcomeEnabled={welcomeEnabled} />
          </div>
        </div>
      ) : (
        card
      )}
    </>
  );
}
