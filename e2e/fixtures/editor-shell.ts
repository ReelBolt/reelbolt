import type { BrowserContext } from '@playwright/test';

/**
 * Which shell of the video editor a test runs in. With no stored choice a tablet, and a desktop account that never
 * customised the editor, start in the Simple shell, so a test that is about the full editor says so before the
 * page loads. The key is only written when nothing is stored, so a test that switches shells and reloads keeps
 * what it switched to.
 */
export type EditorShellChoice = 'full' | 'simple' | 'unset';

export const EDITOR_SHELL_STORAGE_KEY = 'reelbolt:editor:shell';
export const EDITOR_SHELL_INTRO_KEY = 'reelbolt:editor:shell-intro-seen';

export async function pinEditorShell(context: BrowserContext, shell: EditorShellChoice): Promise<void> {
  if (shell === 'unset') return;
  await context.addInitScript(
    ([key, value, introKey]) => {
      try {
        if (window.localStorage.getItem(key) === null) window.localStorage.setItem(key, value);
        // The one-line first-visit note would sit above every screenshot and shift every measured box.
        if (window.localStorage.getItem(introKey) === null) window.localStorage.setItem(introKey, '1');
      } catch {
        /* storage off */
      }
    },
    [EDITOR_SHELL_STORAGE_KEY, shell, EDITOR_SHELL_INTRO_KEY] as const,
  );
}
