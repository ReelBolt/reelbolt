/**
 * Whether the video editor is available in this deployment.
 *
 * Runtime rather than build time, read from the **server** environment per request, mirroring
 * `lib/auth/config.ts` (`SIGNUP_ENABLED`): `web` ships as a precompiled Next standalone server, so a
 * `NEXT_PUBLIC_*` value would have to be baked into the image (the Dockerfile and the CI image build
 * carry no build args for one). Reading plain `process.env` per request means turning the video
 * editor off is a container restart, not a rebuild. Next's Edge sandbox hands the middleware the
 * server's own `process.env` (next/dist/server/web/sandbox/context.js), so `web/proxy.ts` sees the
 * same value the Node pages do.
 *
 * Defaults to **true**: a dev shell, CI and every environment that does not set the flag keep
 * today's behaviour — the editor is available. Only the alpha environment sets it to `false`.
 *
 * This is the switch `web/proxy.ts` gates the video-editor paths on, and the switch the
 * coming-soon page uses to keep itself unreachable while the flag is on. It is a *surface* switch,
 * never an authorization decision: the video editor's API endpoints stay reachable to an
 * authenticated caller, exactly as `alpha-gate.md` row A15 states.
 */
export function videoEditorEnabled(env: Record<string, string | undefined> = process.env): boolean {
  return env.VIDEO_EDITOR_ENABLED !== 'false';
}
