'use client';

import { useSyncExternalStore } from 'react';
import { ActionIcon, Box, useComputedColorScheme, useMantineColorScheme } from '@mantine/core';
import { IconSun, IconMoon } from '@tabler/icons-react';
import { useT } from '@/i18n';

/**
 * Sun/moon toggle. The app starts in `defaultColorScheme="auto"` (web/app/layout.tsx), so a
 * user who has never chosen a scheme has `colorScheme === 'auto'` — the stored value is never
 * the literal `'dark'` even while the page is actually dark, which made the stored-value check
 * render the moon icon on a dark page. The icon therefore follows the COMPUTED scheme (the one
 * the page is really in), and a click writes an EXPLICIT scheme so the first click out of
 * `auto` does what the user sees: dark page → click → light.
 *
 * Hydration: Mantine's provider seeds its scheme from `localStorage` in a state initializer, so
 * on the client's FIRST render `useComputedColorScheme()` already says 'dark' for a stored dark
 * scheme while the server rendered 'auto' → light. Reading it during render made the label and
 * icon differ → React error #418, a client re-render, and an orphaned server-rendered AppShell
 * `<style>` that kept the mobile navbar translated off screen. So the first render always
 * matches the server (light), the icons are switched by CSS (`lightHidden` / `darkHidden`, which
 * follow `data-mantine-color-scheme` set by the boot script), and only the accessible label
 * waits for `mounted`.
 */
const subscribeNever = () => () => {};

export function ThemeToggle() {
  const t = useT('shell.theme');
  const { setColorScheme } = useMantineColorScheme();
  const computedScheme = useComputedColorScheme('light', { getInitialValueInEffect: true });
  // false while rendering for the server and during hydration, true afterwards.
  const mounted = useSyncExternalStore(subscribeNever, () => true, () => false);
  const computed = mounted ? computedScheme : 'light';

  return (
    <ActionIcon
      variant="default"
      size="lg"
      aria-label={computed === 'dark' ? t('toLight') : t('toDark')}
      onClick={() => setColorScheme(computedScheme === 'dark' ? 'light' : 'dark')}
    >
      <Box component="span" lightHidden display="inline-flex">
        <IconSun size={18} />
      </Box>
      <Box component="span" darkHidden display="inline-flex">
        <IconMoon size={18} />
      </Box>
    </ActionIcon>
  );
}
