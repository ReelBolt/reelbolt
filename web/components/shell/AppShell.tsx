"use client";

import {
  AppShell as MantineAppShell,
  Burger,
  Group,
  ScrollArea,
  Title,
} from "@mantine/core";
import { useDisclosure, useMediaQuery } from "@mantine/hooks";
import { usePathname } from "next/navigation";
import { useEffect, useRef } from "react";
import { useAppTouchAttribute, useDeviceClass } from "@/lib/hooks/use-device-class";
import { TABLET_DOWN_QUERY, TABLET_MAX_WIDTH } from "@/lib/breakpoints";
import { useAssistant } from "@/components/assistant/AssistantShellProvider";
import { ActivityIndicator } from "@/components/activity";
import { AssistantTrigger } from "@/components/assistant/AssistantTrigger";
import { AssistantApprovalToast } from "@/components/assistant/AssistantApprovalToast";
import { AssistantReplyToast } from "@/components/assistant/AssistantReplyToast";
import { UnverifiedEmailBanner } from "@/components/auth/UnverifiedEmailBanner";
import { LowBalanceBanner } from "@/components/billing/LowBalanceBanner";
import { FeedbackLauncher } from "@/components/feedback/FeedbackLauncher";
import { OnboardingProvider } from "@/components/onboarding/OnboardingProvider";
import { AlphaBanner } from "./AlphaBanner";
import { OfflineBanner } from "./OfflineBanner";
import { BrandMark } from "./BrandMark";
import { HelpMenu } from "./HelpMenu";
import { InstallPrompt } from "./InstallPrompt";
import { NavLinks } from "./NavLinks";
import classes from "./chrome.module.css";
import headerStyles from "./header.module.css";
import { OrgSwitcher } from "./OrgSwitcher";
import { UserMenu } from "./UserMenu";
import { VideoEditorFlagProvider } from "./VideoEditorFlag";
import { WorkflowAppsFlagProvider } from "./WorkflowAppsFlag";
import { PublicApiFlagProvider } from "./PublicApiFlag";
import { useT } from '@/i18n';

/** Product name: a proper noun, identical in every language. */
const BRAND_NAME = 'ReelBolt';
// Mantine collapses the navbar below this width: 1101 px, so the tablet class (<= 1100) gets the burger.
const NAV_BREAKPOINT = TABLET_MAX_WIDTH + 1;

/**
 * `videoEditorEnabled` arrives from `app/(app)/layout.tsx`, the Server Component that reads the
 * runtime flag. The shell hands it to the nav directly and publishes it to the rest of the tree, so
 * no client component has to read the environment (which a browser bundle cannot see).
 */
export function AppShell({
  children,
  videoEditorEnabled = true,
  workflowAppsEnabled = true,
  publicApiEnabled = false,
  welcomeFlowEnabled = true,
  turnstileSiteKey = '',
}: {
  children: React.ReactNode;
  videoEditorEnabled?: boolean;
  workflowAppsEnabled?: boolean;
  publicApiEnabled?: boolean;
  /** `WELCOME_FLOW_ENABLED`: whether a brand-new account is sent to the welcome questions first. */
  welcomeFlowEnabled?: boolean;
  /** Cloudflare Turnstile site key for the email-resend challenge; '' when not configured. */
  turnstileSiteKey?: string;
}) {
  const t = useT('shell.nav');
  const [opened, { toggle, close }] = useDisclosure();
  const assistant = useAssistant();
  const pathname = usePathname();
  // The timeline editor is a full-bleed work surface: the navbar folds away on every screen size
  // (the burger brings it back over the editor) and the page loses its padding, so every pixel is
  // the editor's. Everywhere else the shell is unchanged.
  const immersive = /^\/editor\/[^/]+\/timeline\/[^/]+\/?$/.test(
    pathname ?? "",
  );
  // On a phone the editor owns the whole screen (RM.6): the app bar and its banners step aside, and the
  // editor's own header carries Back, the name and the save state.
  const deviceClass = useDeviceClass();
  const phoneEditor = immersive && deviceClass === "phone";
  // The welcome questions are one screen at a time with nothing to wander off to: the navbar and
  // the header's tools step aside there. The brand, the account menu and the banners stay.
  const focused = /^\/welcome\/?$/.test(pathname ?? "");
  // Phone chrome: 44px touch targets everywhere except the editor, which sizes its own controls (global.css).
  const touchClass = (pathname ?? "").startsWith("/editor")
    ? undefined
    : "touch-targets";
  // A closed navbar is only translated off screen on a phone; keep it out of the tab order and the
  // accessibility tree until the burger opens it.
  // The sidebar is an overlay on the tablet class and below (<= 1100 px): CSS in chrome.module.css and the
  // Mantine breakpoint below mirror this query (NAV_BREAKPOINT).
  const narrow = useMediaQuery(TABLET_DOWN_QUERY, false);
  useAppTouchAttribute(!(pathname ?? "").startsWith("/editor"));

  // On a phone the navbar is an overlay, so its open state must not outlive the page it was opened
  // on. It used to close only when one of ITS links was tapped: leaving any other way (the back
  // button, a link in the page, a redirect) left it "open" over the next page, so the burger showed
  // an X and the user's next tap closed a menu they never saw — "it acts open but it is not".
  useEffect(() => {
    close();
  }, [pathname, close]);

  // One truth for "the menu is on screen" on a phone. The CSS keys off `data-opened` on the navbar
  // (chrome.module.css), not off Mantine's `--app-shell-navbar-transform` root variable, which a
  // missed inline-style update or a stuck resize transition can leave stale: the burger then showed
  // an X over a navbar that was never painted. `data-opened` deliberately does NOT depend on the
  // JS `narrow` query: the CSS rules are already phone-only (@media), and a `narrow` that lags the
  // viewport would otherwise hide an open menu, the very symptom this guards against.
  const navShown = opened && !phoneEditor && !focused;
  const navOpenOnPhone = narrow && navShown;

  // Same when the app comes back from the background (a phone resumes a tab, restores it from the
  // back/forward cache, or the installed app is thawed): start from a closed menu rather than
  // whatever state was frozen. Every resume signal is listened to, since which one fires varies by
  // browser and by standalone mode. `focus` counts only after the document was actually hidden.
  // A resume event landing right after a burger tap is that same user gesture, not a stale resume,
  // so it must not undo the tap.
  const lastTap = useRef(0);
  useEffect(() => {
    let wasHidden = false;
    const resetIfSettled = () => {
      // The resume signal is consumed either way, so a later window focus cannot replay it.
      wasHidden = false;
      if (Date.now() - lastTap.current < 300) return;
      close();
    };
    const onPageshow = () => resetIfSettled();
    const onResume = () => resetIfSettled();
    const onVisibility = () => {
      if (document.visibilityState === "visible") resetIfSettled();
      else wasHidden = true;
    };
    const onFocus = () => {
      if (wasHidden) resetIfSettled();
    };
    const onPagehide = () => {
      wasHidden = true;
    };
    window.addEventListener("pageshow", onPageshow);
    window.addEventListener("pagehide", onPagehide);
    window.addEventListener("focus", onFocus);
    document.addEventListener("resume", onResume);
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      window.removeEventListener("pageshow", onPageshow);
      window.removeEventListener("pagehide", onPagehide);
      window.removeEventListener("focus", onFocus);
      document.removeEventListener("resume", onResume);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [close]);

  // The navbar opening by ANY path puts it above the assistant sheet, so close the sheet rather than
  // leave two overlays stacked. Keyed on the open state only, so opening the assistant later does not
  // close a menu that is already open.
  const assistantRef = useRef(assistant);
  useEffect(() => {
    assistantRef.current = assistant;
  });
  useEffect(() => {
    if (navOpenOnPhone && assistantRef.current.opened) assistantRef.current.close();
  }, [navOpenOnPhone]);

  // The assistant drawer's modal overlay (z-index 200) sits above the header (100), so on a phone
  // the burger was visible but every tap landed on the overlay and merely dismissed the sheet.
  // While the assistant is open the header is lifted above the overlay and the burger first
  // closes the assistant, then opens the navbar (which would otherwise render under the sheet).
  const handleBurger = () => {
    lastTap.current = Date.now();
    if (assistant.opened) {
      assistant.close();
      if (!opened) toggle();
      return;
    }
    toggle();
  };

  return (
    <OnboardingProvider editorEnabled={videoEditorEnabled} appsEnabled={workflowAppsEnabled} welcomeEnabled={welcomeFlowEnabled}>
      <a href="#main-content" className="skip-link">
        {t('skip')}
      </a>
      <MantineAppShell
        header={{ height: 60, collapsed: phoneEditor }}
        navbar={{
          width: 250,
          breakpoint: NAV_BREAKPOINT,
          collapsed: {
            mobile: !opened || phoneEditor || focused,
            desktop: (immersive && !opened) || phoneEditor || focused,
          },
        }}
        padding={immersive ? 0 : "md"}
      >
        <MantineAppShell.Header
          className={`${classes.header} ${touchClass ?? ""}`}
          style={assistant.opened ? { zIndex: 201 } : undefined}
          hidden={phoneEditor}
          data-testid="app-header"
        >
          <Group h="100%" px="md" justify="space-between" wrap="nowrap">
            <Group wrap="nowrap" className={headerStyles.start}>
              {!focused && (
                <Burger
                  opened={opened}
                  onClick={handleBurger}
                  className={immersive ? undefined : classes.burger}
                  size="sm"
                  aria-label={t('toggle')}
                />
              )}
              <Group gap="xs" wrap="nowrap">
                {/* G2b: the mark is inline (BrandMark) rather than /app/logo.svg, so it is drawn
                  from Mantine's violet ramp instead of the master's fixed hexes. The wordmark
                  beside it carries the accessible name, so the mark itself is aria-hidden. */}
                <BrandMark size={28} />
                {/* The application name sits in the chrome, not in the page, so it must not be a
                  heading: as one it put an <h3> on every page, a screen reader announced
                  "heading level 3, ReelBolt" before the page's own h2, and the document outline
                  gained a section that does not exist. `Title` with `component="p"` keeps
                  order-3's exact typography (same class, same --title-* variables) and changes
                  only the element. */}
                <Title
                  order={3}
                  component="p"
                  c="violet"
                  className="brand-wordmark"
                >
                  {BRAND_NAME}
                </Title>
              </Group>
            </Group>
            <Group gap="xs" wrap="nowrap" className={headerStyles.end}>
              {/* T-019's affordance: the bug/feedback channel, reachable from every page behind /app.
                Deliberately separate from AlphaBanner (T-007), which carries the notice only. */}
              <FeedbackLauncher />
              {!focused && (
                <>
                  <InstallPrompt />
                  <HelpMenu />
                  <ActivityIndicator />
                  <AssistantTrigger />
                </>
              )}
              <UserMenu />
            </Group>
          </Group>
        </MantineAppShell.Header>

        {/* Outside the header's stacking context and below it visually: every page gets the same
          notice about a waiting approval, and it never overlaps the chat composer. */}
        <AssistantApprovalToast />
        <AssistantReplyToast />

        {/* The navbar is a flex column (Mantine's own AppShellNavbar CSS), so a `grow` section
          actually expands and scrolls: 20+ link rows overflowed a 768px viewport before and the
          bottom entries were unreachable. The switcher stays pinned at the top (non-growing),
          the links take the remaining height and scroll; `pb` gives the last row breathing room
          at the scroll edge. NavLinks renders a bare list of siblings, so it keeps rendering
          unchanged here. */}
        <MantineAppShell.Navbar
          p="xs"
          className={`${classes.navbar} ${touchClass ?? ""}`}
          inert={focused || (narrow && !navOpenOnPhone)}
          data-opened={navShown ? "" : undefined}
          aria-hidden={focused || (narrow && !navOpenOnPhone) ? true : undefined}
          data-tour="nav"
        >
          <MantineAppShell.Section mb="xs">
            <OrgSwitcher onSelect={close} />
          </MantineAppShell.Section>
          <MantineAppShell.Section
            grow
            component={ScrollArea}
            type="auto"
            data-testid="nav-links-scroll"
            pb="xs"
          >
            <NavLinks onClick={close} videoEditorEnabled={videoEditorEnabled} publicApiEnabled={publicApiEnabled} />
          </MantineAppShell.Section>
        </MantineAppShell.Navbar>

        <MantineAppShell.Main
          className={touchClass}
          id="main-content"
          tabIndex={-1}
        >
          {/* Above the account notices: the alpha notice is the frame for everything below it, not
            an alert about this account. Dismissible for the session only. */}
          {!immersive && <AlphaBanner />}
          {/* Slim notice while the browser is offline; the editor surface has its own. */}
          {!immersive && <OfflineBanner />}
          {/* Renders nothing for a verified account; the one place the whole dashboard is told an
            address still needs confirming (C7-UI). */}
          {!phoneEditor && <UnverifiedEmailBanner turnstileSiteKey={turnstileSiteKey} />}
          {/* Likewise one place for the whole dashboard to be told the workspace is running out of
            credits — and nothing at all on a deployment without billing (B10c-UI). */}
          {!phoneEditor && <LowBalanceBanner />}
          {/* The page content, and everything it renders, is told the flag once here rather than
            each client component reading an environment a browser bundle cannot see. */}
          <VideoEditorFlagProvider enabled={videoEditorEnabled}>
            <WorkflowAppsFlagProvider enabled={workflowAppsEnabled}>
              <PublicApiFlagProvider enabled={publicApiEnabled}>{children}</PublicApiFlagProvider>
            </WorkflowAppsFlagProvider>
          </VideoEditorFlagProvider>
        </MantineAppShell.Main>
      </MantineAppShell>
    </OnboardingProvider>
  );
}
