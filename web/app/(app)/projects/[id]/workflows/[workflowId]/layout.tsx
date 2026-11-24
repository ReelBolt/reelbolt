import type { Metadata } from 'next';
import { routeTitle } from '@/lib/metadata';
import { getT } from '@/i18n/server';
import { authPageConfig } from '@/lib/auth/config';
import { RunPageConfigProvider } from '@/components/workflow-apps/ConfirmEmailNotice';

// This route's tab title. It is here rather than in the page because the page is a client
// component and Next reads `metadata` from Server Components only; see lib/metadata.ts for why
// the title is an object rather than a bare string.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('projects.meta');
  return { title: routeTitle(t('workflow')) };
}

/**
 * The run page offers "Resend the email" to someone whose address is not confirmed yet. Where the
 * resend is protected by a Turnstile challenge the page needs the site key, which only the server
 * can read: it is read here, per request (every route under `(app)` is rendered per request), and
 * handed down as a value, the same way the shell receives it for its own banner.
 */
export default function WorkflowLayout({ children }: { children: React.ReactNode }) {
  return <RunPageConfigProvider turnstileSiteKey={authPageConfig().turnstileSiteKey}>{children}</RunPageConfigProvider>;
}
