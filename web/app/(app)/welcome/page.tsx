import { redirect } from 'next/navigation';
import { WelcomeFlow } from '@/components/welcome/WelcomeFlow';
import { onboardingSampleClipUrls, welcomeFlowEnabled } from '@/lib/onboarding/config';

// Both switches are read from the environment per request (lib/onboarding/config.ts), so this page
// must not be prerendered at build time. The (app) layout already renders per request; stated here
// too because the page reads the environment itself.
export const dynamic = 'force-dynamic';

/** A project id as the API issues it; anything else in `?project=` is ignored. */
const PROJECT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * `/app/welcome`: the two welcome questions and the first project (docs/design/new-user-funnel.md
 * section 3, step 4). A signed-in page like every other under `(app)`: `web/proxy.ts` sends a
 * visitor without a session to sign in first.
 *
 * With `WELCOME_FLOW_ENABLED=false` the page answers with the dashboard, and the dashboard never
 * sends anyone here, so the two cannot bounce a person between them.
 *
 * `?step=shots&project=<id>` reopens the shot list of a project that starts from an idea (the
 * dashboard links to it).
 */
export default async function WelcomePage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  if (!welcomeFlowEnabled()) redirect('/dashboard');
  const params = await searchParams;
  const project = typeof params.project === 'string' && PROJECT_ID.test(params.project) ? params.project : null;
  const shotsForProjectId = params.step === 'shots' ? project : null;
  return <WelcomeFlow sampleClipUrls={onboardingSampleClipUrls()} shotsForProjectId={shotsForProjectId} />;
}
