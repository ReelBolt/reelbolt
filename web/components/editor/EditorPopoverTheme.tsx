'use client';

import { MantineThemeProvider } from '@mantine/core';
import { EDITOR_BOTTOM_CHROME_PX } from '@/app/theme';

// Mantine's own padding: shift keeps 5 px from every edge, flip has none. Only the bottom differs in the editor.
const EDITOR_POPOVER_THEME = {
  components: {
    Popover: {
      defaultProps: {
        middlewares: {
          flip: { padding: { bottom: EDITOR_BOTTOM_CHROME_PX } },
          shift: { padding: { top: 5, left: 5, right: 5, bottom: EDITOR_BOTTOM_CHROME_PX } },
        },
      },
    },
  },
};

/** Editor routes only: dropdowns/menus flip upward instead of landing under the status bar / phone dock. */
export function EditorPopoverTheme({ children }: { children: React.ReactNode }) {
  return <MantineThemeProvider theme={EDITOR_POPOVER_THEME}>{children}</MantineThemeProvider>;
}
