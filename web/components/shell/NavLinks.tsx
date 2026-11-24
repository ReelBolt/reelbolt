'use client';

import { useId, useMemo, useState } from 'react';
import { Group, NavLink, Text, UnstyledButton } from '@mantine/core';
import {
  IconDashboard,
  IconFolder,
  IconRobot,
  IconUsers,
  IconSettings,
  IconBroadcast,
  IconPlugConnected,
  IconBook,
  IconTimeline,
  IconAdjustments,
  IconPalette,
  IconChartBar,
  IconReceipt,
  IconCreditCard,
  IconDeviceDesktop,
  IconUserCircle,
  IconChevronRight,
  IconBellRinging,
  IconColorSwatch,
  IconDatabase,
  IconDiscount,
  IconMail,
  IconShieldCheck,
  IconShieldLock,
  IconKey,
  IconLink,
  IconSend,
  IconBooks,
} from '@tabler/icons-react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAvatarLibraryAvailable } from '@/lib/hooks/use-avatar-library';
import classes from './chrome.module.css';
import { useT, msg, type Msg } from '@/i18n';

/** The icon component type, taken from one member so no extra type import is needed. */
type NavIcon = typeof IconDashboard;

interface NavItem {
  label: string | Msg;
  href: string;
  icon: NavIcon;
  /**
   * Match the route exactly rather than by prefix. Required wherever one entry's href is a prefix
   * of another's: '/settings' would otherwise light up on the Usage and Plan & billing pages too.
   */
  exact?: boolean;
}

interface NavSection {
  /** Stable id: it is the React key, the panel's DOM id suffix and the collapse-state key. */
  id: string;
  label: string | Msg;
  items: NavItem[];
}

/**
 * The pinned band: the loop a user came for, always on screen, never behind a disclosure.
 *
 * Five of these six are also the entries the shipped user guide names as top-level menu items
 * (docs/user-guide/getting-started.md), so keeping them one click from anywhere is what keeps that
 * page true. Runners is here for the same reason the guide cites it bare (docs/user-guide/
 * account/desktop-runner.md): it is shared rather than admin-only, because the list is readable by
 * every member of the active workspace and revoking additionally allows the user who paired the
 * device -- a rule this menu cannot evaluate for them.
 */
// Where the first-run tour points (`data-tour` on the matching row).
const TOUR_ANCHORS: Record<string, string> = { '/projects': 'nav-projects', '/editor': 'nav-editor' };

const primaryLinks: NavItem[] = [
  { label: msg('shell.nav.dashboard'), href: '/dashboard', icon: IconDashboard },
  { label: msg('shell.nav.projects'), href: '/projects', icon: IconFolder },
  { label: msg('shell.nav.editor'), href: '/editor', icon: IconTimeline },
  { label: msg('shell.nav.publishing'), href: '/publishing', icon: IconSend },
];

// The collapsible "More" section (id 'more'): collapsed unless the current route is inside it.
const moreLinks: NavItem[] = [
  { label: msg('shell.nav.styles'), href: '/styles', icon: IconPalette },
  { label: msg('shell.nav.runners'), href: '/runners', icon: IconDeviceDesktop },
  { label: msg('shell.nav.agents'), href: '/agents', icon: IconRobot },
];

/**
 * The settings surface, as its own group.
 *
 * This grouping is not invented here. The user guide has always described these two pages as
 * "Settings -> Usage" and "Settings -> Plan and billing" (docs/user-guide/account/plans-and-credits.md,
 * workspaces-and-teams.md) and they have always lived under /settings/*; grouping them under the
 * name the documentation already uses is what makes those instructions match the screen.
 *
 * "Account" is the caller's own page -- you, not the workspace. The other two act on the active
 * workspace, which is why they sit in one group rather than because they are the same kind of thing.
 */
const AVATAR_LIBRARY_HREF = '/settings/avatar-library';

const settingsLinks: NavItem[] = [
  { label: msg('shell.nav.account'), href: '/settings', icon: IconUserCircle, exact: true },
  // Export and account deletion on their own page, the one the legal gate exempts (S-22).
  { label: msg('shell.nav.data'), href: '/settings/data', icon: IconDatabase },
  { label: msg('shell.nav.usage'), href: '/settings/usage', icon: IconChartBar },
  { label: msg('shell.nav.billing'), href: '/settings/billing', icon: IconCreditCard },
  { label: msg('shell.nav.notifications'), href: '/settings/notifications', icon: IconBellRinging },
  // The active workspace's shared brand libraries (SPEC §16.1, P4-06). Deliberately in the
  // always-visible group rather than the org-admin one: every member may read the list, a Member
  // is read-only, and Billing is told the page is not available. The page gates the writes.
  { label: msg('shell.nav.brandLibraries'), href: '/brand-libraries', icon: IconColorSwatch },
  // The active workspace's avatar library (docs/design/avatar-objects.md). In the always-visible group for the
  // same reason: every member may read it and the page gates the writes. Listed only while the server answers
  // the library (both avatar switches off = no library, so no link to a page that could not load).
  { label: msg('shell.nav.avatarLibrary'), href: AVATAR_LIBRARY_HREF, icon: IconBooks },
];

// Workspace group (A15-UI; the id stays 'organization'): the active workspace's settings and members, shown to
// org Admin+ (Owner or Admin; Billing and Member are excluded). The pages ship with A16-UI.
const orgLinks: NavItem[] = [
  { label: msg('shell.nav.orgSettings'), href: '/org/settings', icon: IconSettings },
  { label: msg('shell.nav.members'), href: '/org/members', icon: IconUsers },
  // The workspace's own (bring-your-own) inference providers (A17-UI).
  { label: msg('shell.nav.providers'), href: '/org/inference-providers', icon: IconPlugConnected },
  // The public API's workspace keys, only when the deployment opts in (PUBLIC_API_ENABLED).
  { label: msg('shell.nav.apiKeys'), href: '/org/api-keys', icon: IconKey },
];

// Platform group (A15-UI: renamed from "Admin"): platform-wide administration, shown only
// to platform admins.
const platformLinks: NavItem[] = [
  { label: 'Overview', href: '/admin', icon: IconSettings, exact: true },
  { label: 'Workflow Service', href: '/admin/workflow-service', icon: IconBroadcast },
  { label: 'Inference', href: '/admin/inference-providers', icon: IconPlugConnected },
  { label: 'Billing', href: '/admin/billing', icon: IconReceipt },
  { label: 'Promotions', href: '/admin/promotions', icon: IconDiscount },
  { label: 'Campaigns', href: '/admin/campaigns', icon: IconMail },
  // Tracked links and their funnel (docs/design/new-user-funnel.md); the label is in the adminLinks catalogue.
  { label: msg('adminLinks.nav'), href: '/admin/links', icon: IconLink },
  { label: 'Content safety', href: '/admin/csam', icon: IconShieldLock },
  { label: 'Calibration', href: '/admin/calibration', icon: IconAdjustments },
  { label: 'Content moderation', href: '/admin/moderation', icon: IconShieldCheck },
  { label: 'Community recipes', href: '/admin/marketplace', icon: IconShieldCheck },
  { label: 'Skills', href: '/admin/skills', icon: IconBook },
  { label: 'Users', href: '/admin/users', icon: IconUsers },
];

// Dense navbar rows: the platform-admin list was 20 rows + 2 group labels, which overflowed a
// 768px-tall viewport and left the bottom entries unreachable. The rows stay compacted: 8px vertical
// padding (about 36px total, so the touch target stays at least 36px) and 18px icons. Only
// root/label/section are set; the theme still owns the hover and active surfaces.
const linkStyles = {
  root: { padding: '8px var(--mantine-spacing-sm)' },
  label: { fontSize: 'var(--mantine-font-size-sm)', lineHeight: 1.3 },
  section: { '> svg': { width: 18, height: 18 } },
};

/**
 * The section header is a real disclosure button, and that is the whole accessibility argument for
 * this menu.
 *
 * Its predecessor was a disabled NavLink: a pointer-events-none, non-focusable line at 40% opacity.
 * Collapsing a section behind a control like that would strand the items -- no role, no state, no
 * keyboard path back. What ships instead is a focusable <button> carrying aria-expanded (the state a
 * screen reader announces) and aria-controls (the panel it owns, which stays in the DOM and is hidden
 * with the hidden attribute), so "collapsed" is a state the user can always leave with Tab + Enter,
 * never a dead end.
 *
 * It is deliberately a plain Text line inside the button rather than a heading: the app name in the
 * header was fixed for putting an <h3> on every page and polluting the document outline (commit
 * f7d55d643), and a nav heading per group would reintroduce exactly that on every route.
 */
const sectionToggleStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 4,
  width: '100%',
  minHeight: 'var(--nav-toggle-min-height, 36px)',
  padding: '8px var(--mantine-spacing-sm)',
  borderRadius: 'var(--mantine-radius-default)',
  textAlign: 'left',
};

/**
 * Section labels.
 *
 * The colour is the one thing about the old group label that was measured and found wanting. It used
 * --mantine-color-dimmed, which resolves to 3.32:1 on white and 4.04:1 on the dark surface -- both
 * under the 4.5:1 WCAG AA floor for text this size (12px, weight 600 is not "large text"). That is
 * the label the earlier accessibility pass left alone; it is fixed here rather than project-wide
 * because these three lines just became the interactive affordance for the whole compaction, and a
 * control's name has to be readable.
 *
 * The pair is scheme-aware through the tokens rather than swapped in JS: gray-7 on light is 7.9:1 and
 * gray-5 on dark is 7.4:1, both AAA, and still clearly subordinate to the 14px link labels beside
 * them. The "current page is in here" variant uses --mantine-color-violet-text, which Mantine already
 * resolves per scheme (4.95:1 light, 4.61:1 dark) -- also AA.
 */
// Label type and colour live in chrome.module.css (.sectionLabel / .sectionLabelCurrent): mono eyebrow, scheme-aware tokens.

/** An exact entry matches its own route; a prefix entry matches it and everything nested under it. */
function isActive(item: NavItem, pathname: string): boolean {
  if (item.exact) return pathname === item.href;
  return pathname === item.href || pathname.startsWith(item.href + '/');
}

function sectionContains(section: NavSection, pathname: string): boolean {
  return section.items.some((item) => isActive(item, pathname));
}

/** An explicit choice, and the route the user made it on. */
interface SectionChoice {
  open: boolean;
  at: string;
}

/**
 * Whether a section is showing, resolved entirely from the route and whatever the user last said.
 *
 * There is no effect and no stored route in this component: all three rules below are pure, so the
 * menu can never disagree with the page it is next to, and nothing has to be synchronised after a
 * navigation. The three rules, in order:
 *
 *   1. No choice -> open only where you are. That is the compact default: six destinations and three
 *      named groups on the first paint instead of twenty flat rows.
 *   2. A choice made on the page you are STILL on is the answer either way. This is the whole reason
 *      the route is recorded: without it, collapsing the group that holds the current page would be
 *      undone by rule 3 on the very next render, and the toggle would be a dead control.
 *   3. A choice made elsewhere: opening is sticky (you said you wanted it, it stays), collapsing is
 *      not (arrival by a breadcrumb, the account menu, or a URL somebody pasted must not land you on
 *      a page whose own entry is hidden).
 *
 * The renderer AND the toggle both go through this one function. Deriving the two separately is how
 * the first draft shipped exactly that dead control: the toggle asked "is it open?" one way and the
 * renderer asked it another.
 */
function sectionOpen(
  section: NavSection,
  pathname: string,
  choice: SectionChoice | undefined,
): boolean {
  if (!choice) return sectionContains(section, pathname);
  if (choice.at === pathname) return choice.open;
  if (choice.open) return true;
  return sectionContains(section, pathname);
}

/**
 * `videoEditorEnabled` is threaded from `app/(app)/layout.tsx` through the shell, never read from
 * the environment here: this is a client component, and a browser bundle cannot see the server-side
 * flag (see VideoEditorFlag.tsx). The default keeps the editor — the flag's own default — for a
 * render outside the shell. With it off, the Editor entry becomes a plain label rather than a link
 * the middleware would then have to bounce.
 */
export function NavLinks({
  onClick,
  videoEditorEnabled = true,
  publicApiEnabled = false,
}: {
  onClick?: () => void;
  videoEditorEnabled?: boolean;
  publicApiEnabled?: boolean;
}) {
  const pathname = usePathname();
  const t = useT();
  const { isPlatformAdmin, isOrgAdmin } = useAuth();
  const avatarLibrary = useAvatarLibraryAvailable();
  const sectionIdPrefix = useId();
  const text = (label: string | Msg) => (typeof label === 'string' ? label : t.msg(label));

  const sections = useMemo(() => {
    const list: NavSection[] = [{
      id: 'more',
      label: msg('shell.nav.sectionMore'),
      items: moreLinks,
    }, {
      id: 'settings',
      label: msg('shell.nav.sectionSettings'),
      items: avatarLibrary ? settingsLinks : settingsLinks.filter((link) => link.href !== AVATAR_LIBRARY_HREF),
    }];
    if (isOrgAdmin) {
      list.push({
        id: 'organization',
        label: msg('shell.nav.sectionWorkspace'),
        items: publicApiEnabled ? orgLinks : orgLinks.filter((link) => link.href !== '/org/api-keys'),
      });
    }
    if (isPlatformAdmin) list.push({ id: 'platform', label: 'Platform', items: platformLinks });
    return list;
  }, [isOrgAdmin, isPlatformAdmin, publicApiEnabled, avatarLibrary]);

  /**
   * Only EXPLICIT choices are stored; an absent key means "no opinion", which sectionOpen resolves
   * against the current route at render time. Nothing is derived from anything client-only, so the
   * server-rendered markup and the first client render agree and there is nothing to hydrate around.
   */
  const [choices, setChoices] = useState<Record<string, SectionChoice>>({});

  const toggle = (section: NavSection) => {
    setChoices((prev) => ({
      ...prev,
      [section.id]: {
        open: !sectionOpen(section, pathname, prev[section.id]),
        at: pathname,
      },
    }));
  };

  return (
    <>
      {primaryLinks.map((link) =>
        link.href === '/editor' && !videoEditorEnabled ? (
          <Group
            key={link.href}
            gap="sm"
            wrap="nowrap"
            style={linkStyles.root}
            data-testid="video-editor-coming-soon"
          >
            <IconTimeline size={18} aria-hidden="true" />
            <Text size="sm" c="dimmed">{t('shell.nav.videoEditorSoon')}</Text>
          </Group>
        ) : (
          <NavLink
            key={link.href}
            component={Link}
            href={link.href}
            label={text(link.label)}
            leftSection={<link.icon size={18} />}
            active={isActive(link, pathname)}
            onClick={onClick}
            styles={linkStyles}
            data-tour={TOUR_ANCHORS[link.href]}
          />
        ),
      )}

      {sections.map((section) => {
        const containsCurrent = sectionContains(section, pathname);
        const open = sectionOpen(section, pathname, choices[section.id]);
        const panelId = sectionIdPrefix + '-' + section.id;

        return (
          <div key={section.id}>
            <UnstyledButton
              type="button"
              onClick={() => toggle(section)}
              aria-expanded={open}
              aria-controls={panelId}
              // Marks a COLLAPSED group that holds the page you are on, so collapsing it does not
              // lose "you are here" entirely. Also what the test asserts on, in place of a colour.
              data-current={!open && containsCurrent ? 'true' : undefined}
              style={sectionToggleStyle}
            >
              <Text
                className={!open && containsCurrent ? `${classes.sectionLabel} ${classes.sectionLabelCurrent}` : classes.sectionLabel}
              >
                {text(section.label)}
              </Text>
              <IconChevronRight
                size={14}
                aria-hidden="true"
                style={{
                  flexShrink: 0,
                  transition: 'transform 150ms ease',
                  transform: open ? 'rotate(90deg)' : undefined,
                }}
              />
            </UnstyledButton>
            {/* Rendered always and hidden with the attribute rather than unmounted: aria-controls keeps
                a real target, and hidden takes the links out of both the tab order and the
                accessibility tree while the toggle above stays focusable and announces the state. */}
            <div id={panelId} role="group" aria-label={text(section.label)} hidden={!open}>
              {section.items.map((link) => (
                <NavLink
                  key={link.href}
                  component={Link}
                  href={link.href}
                  label={text(link.label)}
                  leftSection={<link.icon size={18} />}
                  active={isActive(link, pathname)}
                  onClick={onClick}
                  styles={linkStyles}
                />
              ))}
            </div>
          </div>
        );
      })}
    </>
  );
}
