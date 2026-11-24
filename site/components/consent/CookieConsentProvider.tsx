'use client';

import { createContext, useContext, useSyncExternalStore, type ReactNode } from 'react';
import { CONSENT_STORAGE_KEY, readStoredConsent, type ConsentValue } from '@/lib/consent';

export type { ConsentValue };

interface CookieConsentContextValue {
  consent: ConsentValue;
  accept: () => void;
  reject: () => void;
  reopen: () => void;
}

const CookieConsentContext = createContext<CookieConsentContextValue | undefined>(undefined);

// getServerSnapshot returns null so the server render and the first client render match — the
// real value (if any) is only read from localStorage once mounted, via useSyncExternalStore.
function getServerSnapshot(): ConsentValue {
  return null;
}

const listeners = new Set<() => void>();

function notify() {
  listeners.forEach((listener) => listener());
}

// `storage` only fires in OTHER tabs, so cross-tab changes reach us there, while `notify()` below
// (called from `writeConsent`) covers this tab's own accept/reject/reopen calls.
function subscribe(callback: () => void) {
  listeners.add(callback);
  window.addEventListener('storage', callback);
  return () => {
    listeners.delete(callback);
    window.removeEventListener('storage', callback);
  };
}

function writeConsent(value: ConsentValue) {
  try {
    if (value === null) {
      window.localStorage.removeItem(CONSENT_STORAGE_KEY);
    } else {
      window.localStorage.setItem(CONSENT_STORAGE_KEY, value);
    }
  } catch {
    // Ignore write failures. The choice still holds in memory once notify() has re-rendered this
    // tab; it simply won't survive a reload. And because lib/consent.ts is what both this provider
    // and trackEvent read, a failed write means no choice is found and no event is sent — the
    // safe direction for a failure to be wrong in.
  }
  notify();
}

export function CookieConsentProvider({ children }: { children: ReactNode }) {
  const consent = useSyncExternalStore(subscribe, readStoredConsent, getServerSnapshot);

  function accept() {
    writeConsent('granted');
  }

  function reject() {
    writeConsent('denied');
  }

  function reopen() {
    writeConsent(null);
  }

  return (
    <CookieConsentContext.Provider value={{ consent, accept, reject, reopen }}>
      {children}
    </CookieConsentContext.Provider>
  );
}

export function useCookieConsent(): CookieConsentContextValue {
  const context = useContext(CookieConsentContext);
  if (!context) {
    throw new Error('useCookieConsent must be used within a CookieConsentProvider');
  }
  return context;
}
