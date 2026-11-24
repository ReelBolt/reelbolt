import { Card } from '@mantine/core';
import { cookies } from 'next/headers';
import { AcceptTermsForm } from '@/components/auth/AcceptTermsForm';
import { LegalGateExits } from '@/components/auth/LegalGateExits';
import { legalChangeFor, type AcceptedLegalVersions } from '@/lib/auth/legal';
import { parseUserCookie } from '@/lib/auth/user-cookie';
import { servedLegalVersions } from '@/lib/auth/legal-server';

// Not prerendered: the version shown and submitted is read from the API at request time (F-7), and
// a baked copy of it would be exactly the stale constant this replaces.
export const dynamic = 'force-dynamic';

/** The accepted versions the readable session cookie carries, or null when it cannot be read. */
function acceptedFromCookie(raw: string | undefined): AcceptedLegalVersions | null {
  const user = parseUserCookie(raw);
  if (!user) return null;
  return {
    termsVersion: typeof user.termsVersion === 'string' ? user.termsVersion : undefined,
    privacyVersion: typeof user.privacyVersion === 'string' ? user.privacyVersion : undefined,
  };
}

export default async function AcceptTermsPage() {
  // Reached because the proxy gate found this session behind, so the API is answering; a null here
  // (an outage between the redirect and this render) still renders, and the form carries whatever
  // the refusal answers with.
  const [legal, cookieStore] = await Promise.all([servedLegalVersions(), cookies()]);
  // Only a session KNOWN to be current on the Terms and behind on Privacy alone gets the
  // acknowledgement; anything else (a first OAuth login, an unreadable cookie, no answer from the
  // API) gets the explicit acceptance, which is the conservative of the two.
  const accepted = acceptedFromCookie(cookieStore.get('reelbolt_user')?.value);
  const change = legalChangeFor(accepted, legal);
  // A readable session that has accepted nothing yet: welcome wording, not "we updated" (A26).
  const firstAcceptance = accepted !== null && accepted.termsVersion === undefined;

  return (
    <Card radius="md" p="xl" withBorder>
      <AcceptTermsForm
        termsVersion={legal?.termsVersion ?? ''}
        privacyVersion={legal?.privacyVersion ?? ''}
        change={change === 'privacy' ? 'privacy' : 'terms'}
        firstAcceptance={firstAcceptance}
      />
      <LegalGateExits />
    </Card>
  );
}
