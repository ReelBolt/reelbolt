'use client';

import { useCallback, useEffect, useState } from 'react';
import { clearSignupIntent, readSignupIntent, type SignupIntent } from '../auth/signup-intent';
import { userKeyFromEmail } from '../telemetry/identity';
import { useAuth } from './use-auth';

export interface SignupIntentState {
  /** The browser has been asked (the person is known); until then nothing may act on "no intent". */
  ready: boolean;
  /** What the signed-in person asked for before they had an account, or null. */
  intent: SignupIntent | null;
  /** Forgets one of the two values once it was used or declined. */
  clear: (part: keyof SignupIntent) => void;
}

/**
 * The plan and the return path the signed-in person's sign-up link carried
 * (`lib/auth/signup-intent.ts`), read once after mount. Read in an effect, never while rendering:
 * the server renders without it, and the first client render must match.
 */
export function useSignupIntent(): SignupIntentState {
  const { user } = useAuth();
  const userKey = userKeyFromEmail(user?.email);
  const [state, setState] = useState<{ forKey: string; intent: SignupIntent | null }>({ forKey: '', intent: null });

  useEffect(() => {
    if (!userKey) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- hydrate from storage after mount to match the server markup
    setState({ forKey: userKey, intent: readSignupIntent(userKey) });
  }, [userKey]);

  const clear = useCallback((part: keyof SignupIntent) => {
    clearSignupIntent(part);
    setState((current) => {
      if (!current.intent) return current;
      const rest = { ...current.intent };
      delete rest[part];
      return { ...current, intent: rest.plan || rest.next ? rest : null };
    });
  }, []);

  const ready = userKey !== '' && state.forKey === userKey;
  return { ready, intent: ready ? state.intent : null, clear };
}
