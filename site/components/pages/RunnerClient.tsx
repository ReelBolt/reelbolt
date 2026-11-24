'use client';

import { useState, useSyncExternalStore } from 'react';
import { useT } from '@/i18n';
import { buttonClass } from '@/components/ui/button-styles';
import type { RunnerPlatform } from '@/lib/runner-latest';

export interface PrimaryDownload {
  platform: RunnerPlatform;
  platformName: string;
  url: string;
  label: string;
}

function detectPlatform(): RunnerPlatform | null {
  const nav = navigator as Navigator & { userAgentData?: { platform?: string } };
  const ua = `${nav.userAgentData?.platform ?? ''} ${navigator.userAgent}`.toLowerCase();
  if (ua.includes('win')) return 'windows-x64';
  if (ua.includes('android') || ua.includes('iphone') || ua.includes('ipad') || ua.includes('mac')) return null;
  if (ua.includes('linux') || ua.includes('x11')) {
    return /aarch64|arm64/.test(ua) ? 'linux-arm64' : 'linux-x64';
  }
  return null;
}

function subscribe(): () => void {
  return () => {};
}

/**
 * Progressive enhancement: the full list of downloads is static markup, so this only adds a
 * primary button for the visitor's own platform when JS runs and a matching installer exists.
 */
export function DetectedDownload({ primaries }: { primaries: readonly PrimaryDownload[] }) {
  const t = useT('runner.client');
  const platform = useSyncExternalStore(subscribe, detectPlatform, () => null);
  const match = primaries.find((p) => p.platform === platform);
  if (!match) return null;
  return (
    <div className="mt-6">
      <a href={match.url} className={buttonClass({ variant: 'accent', size: 'lg' })}>
        {t('downloadFor', { platform: match.platformName })}
      </a>
      <p className="mt-2 font-mono text-xs text-ink-muted">
        {t('otherPlatforms', { label: match.label })}
      </p>
    </div>
  );
}

export function CopyButton({ text, label }: { text: string; label: string }) {
  const t = useT('runner.client');
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      aria-label={label}
      className="border border-line px-3 py-1 font-mono text-xs uppercase tracking-eyebrow text-ink hover:bg-paper-2 focus-visible:outline-2 focus-visible:outline-offset-2"
      onClick={() => {
        navigator.clipboard
          .writeText(text)
          .then(() => {
            setCopied(true);
            window.setTimeout(() => setCopied(false), 2000);
          })
          .catch(() => setCopied(false));
      }}
    >
      {copied ? t('copied') : t('copy')}
      <span role="status" aria-live="polite" className="sr-only">
        {copied ? t('copiedStatus') : ''}
      </span>
    </button>
  );
}
