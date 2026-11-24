/**
 * Runtime switches of the welcome flow, read from the **server** environment per request, exactly
 * like `lib/workflow-apps/config.ts` (`WORKFLOW_APPS_ENABLED`): a `NEXT_PUBLIC_*` value would be
 * baked into the image at build time, so a plain `process.env` read keeps each of these a container
 * restart, not a rebuild. Client components receive the values as props (see `app/(app)/layout.tsx`
 * and `app/(app)/welcome/page.tsx`); none of them reads the environment.
 */

/**
 * Whether a brand-new account is sent to `/welcome` (two questions, then its first project).
 *
 * Defaults to **true**. `WELCOME_FLOW_ENABLED=false` switches the questions off, and only them:
 *
 *  - nobody is sent to `/welcome`, and the page itself answers with the dashboard;
 *  - the first-run tour starts by itself as it did before the welcome flow existed;
 *  - the signup page and the check-email panel no longer promise "two quick questions";
 *  - the dashboard keeps its "your first video" rail (it is not the dashboard of before this
 *    release: that panel is gone). With no project its Start button opens the plain "New project"
 *    form, which asks what the project starts from, so the first app still fits what the person has.
 *
 * It is therefore a switch for the questions, not a rollback of the release. A surface switch,
 * never an authorization decision: the onboarding endpoints stay reachable to an authenticated caller.
 */
export function welcomeFlowEnabled(env: Record<string, string | undefined> = process.env): boolean {
  return env.WELCOME_FLOW_ENABLED !== 'false';
}

/** The most sample clips offered: the footage recipe takes 1 to 20 clips, a first try needs far fewer. */
export const MAX_SAMPLE_CLIPS = 8;

/**
 * The operator's sample clips for "No clips handy? Try it with our sample clips", from
 * `ONBOARDING_SAMPLE_CLIP_URLS`: public `https://` links separated by commas or white space.
 *
 * Defaults to **none**, and with none the offer is not rendered at all: ReelBolt ships no sample
 * clip of its own. Anything that is not a plain `https://` link (another scheme, credentials in the
 * link, something unparsable) is dropped, duplicates are dropped, and at most
 * {@link MAX_SAMPLE_CLIPS} are kept. The clips are imported with the ordinary import-from-link API,
 * which applies its own checks (public address, size, quota, a verified email).
 */
export function onboardingSampleClipUrls(env: Record<string, string | undefined> = process.env): string[] {
  const out: string[] = [];
  for (const raw of (env.ONBOARDING_SAMPLE_CLIP_URLS ?? '').split(/[\s,]+/)) {
    if (!raw) continue;
    let url: URL;
    try {
      url = new URL(raw);
    } catch {
      continue;
    }
    if (url.protocol !== 'https:' || url.username || url.password) continue;
    if (!out.includes(url.href)) out.push(url.href);
    if (out.length === MAX_SAMPLE_CLIPS) break;
  }
  return out;
}
