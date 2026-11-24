'use client';

import { Button, Stack, Text } from '@mantine/core';
import { sendSignupSubmitted } from '@/components/analytics/SignupTracker';
import { providerRefusesEmbedded } from '@/lib/auth/in-app-browser';
import { oauthProviderLabel } from '@/lib/auth/oauth';
import { oauthStartHref, type SignupCarry } from '@/lib/auth/signup-carry';
import { rememberSignupIntent } from '@/lib/auth/signup-intent';
import { useT } from '@/i18n';

interface OAuthButtonsProps {
  providers: string[];
  /**
   * What the visitor's own link brought (lib/auth/signup-carry.ts). Its persona and campaign
   * parameters ride the href as `for` and `utm_*`: a provider sign-in has no request body, so the
   * start endpoint packs them into the OAuth `state` and reads them back in the callback.
   */
  carry?: SignupCarry;
  /**
   * True on the signup page, where pressing a provider button IS asking for an account: the press
   * sends the consented `signup.submitted` visitor event, exactly as submitting the form does.
   * Without it the visitor step "Submitted signup" would count only people who chose a password,
   * and everyone who signed up with a provider would read as having left the page. Left false on
   * the sign-in page: most presses there are existing accounts signing in.
   */
  signup?: boolean;
  /**
   * The page is open inside another app's built-in browser (lib/auth/in-app-browser.ts). A provider
   * that refuses sign-in there is not offered as a button that leads to its error page; a sentence
   * says how to open the page in the real browser instead.
   */
  inAppBrowser?: boolean;
}

/**
 * Sign-in buttons for the configured OAuth providers (C10).
 *
 * Plain anchors, deliberately not next/link: the Go API lives at the domain root while this app is
 * served under the '/app' basePath, and a next/link href would get '/app' prepended and 404
 * (the same reason lib/auth/legal.ts uses plain anchors for the marketing site's legal pages).
 * The route is a top-level navigation on purpose — the visitor leaves Next, is redirected to the
 * provider and comes back through /api/v1/auth/oauth/{provider}/callback with the session cookies set.
 *
 * Renders nothing when no provider is configured, so self-host deployments (no credentials) never
 * see a button whose endpoint answers 404.
 *
 * A press also remembers the plan and the return path of the visitor's link for the way back
 * (lib/auth/signup-intent.ts): the provider round trip cannot carry them. Neither that nor the
 * beacon ever delays or blocks the navigation.
 */
export function OAuthButtons({ providers, carry, signup = false, inAppBrowser = false }: OAuthButtonsProps) {
  // The root translator: the provider labels live under libLabels, in the visitor's language.
  const t = useT();
  if (providers.length === 0) return null;

  const offered = inAppBrowser ? providers.filter((provider) => !providerRefusesEmbedded(provider)) : providers;
  const refused = providers.length - offered.length;

  const onPress = () => {
    if (carry) rememberSignupIntent(carry);
    if (signup) sendSignupSubmitted();
  };

  return (
    <Stack gap="xs">
      {offered.map((provider) => (
        <Button
          key={provider}
          component="a"
          href={oauthStartHref(provider, carry)}
          variant="default"
          fullWidth
          onClick={onPress}
        >
          {oauthProviderLabel(provider, t)}
        </Button>
      ))}
      {refused > 0 && (
        <Text size="sm" c="dimmed" role="note" data-testid="oauth-in-app-note">
          {t('auth.oauth.inAppGoogle')}
        </Text>
      )}
    </Stack>
  );
}
