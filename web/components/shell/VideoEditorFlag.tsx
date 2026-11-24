'use client';

import { createContext, useContext, type ReactNode } from 'react';
import { msg } from '@/i18n/msg';

/**
 * The one label every entry point shows in place of the video editor while the flag is off, so the
 * nav and a render's "Open in Editor" cannot drift apart.
 */
export const VIDEO_EDITOR_COMING_SOON = msg('shell.nav.videoEditorSoon');

/**
 * @deprecated English-only string kept so call sites outside the shell keep compiling until they
 * resolve `VIDEO_EDITOR_COMING_SOON` with `useT().msg()`.
 */
export const VIDEO_EDITOR_COMING_SOON_LABEL = 'Video editor · Coming soon';

/**
 * The flag reaches client components as a value, never as an env read.
 *
 * A browser bundle cannot see a server-side `VIDEO_EDITOR_ENABLED`: Next inlines only
 * `NEXT_PUBLIC_*` into the client, and `web`'s image is built with no build args (see
 * lib/editor/config.ts). A component that read the env directly therefore rendered the wrong thing
 * after hydration — and, for a flag that is off, it would offer the very link the gate exists to
 * hide. So `app/(app)/layout.tsx` (a Server Component) reads the flag once and the shell provides
 * it here; every client component that must hide the editor reads this context.
 *
 * The default is `true` — the flag's own default — so a component rendered outside the shell (a
 * unit test, a story) keeps today's behaviour instead of hiding the editor by accident.
 */
const VideoEditorFlagContext = createContext(true);

export function VideoEditorFlagProvider({
  enabled,
  children,
}: {
  enabled: boolean;
  children: ReactNode;
}) {
  return <VideoEditorFlagContext.Provider value={enabled}>{children}</VideoEditorFlagContext.Provider>;
}

/** Whether the video editor is available, as the shell resolved it on the server. */
export function useVideoEditorEnabled(): boolean {
  return useContext(VideoEditorFlagContext);
}
