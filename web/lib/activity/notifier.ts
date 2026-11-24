'use client';

import { createElement, useEffect, useRef, useSyncExternalStore } from 'react';
import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { notifications } from '@mantine/notifications';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import { getNotifySwitch, notificationPermission, showAssistantNotification } from '@/lib/assistant/browser-notify';
import { getJobType, jobHref } from './registry';
import type { ActivityJob } from './types';

/** Per-device switch "Tell me when a background job finishes". Default on. */
export const ACTIVITY_TOASTS_KEY = 'reelbolt.activity.toasts';

const toastListeners = new Set<() => void>();

export function getActivityToasts(): boolean {
  try {
    return window.localStorage.getItem(ACTIVITY_TOASTS_KEY) !== '0';
  } catch {
    return true;
  }
}

export function setActivityToasts(on: boolean): void {
  try {
    window.localStorage.setItem(ACTIVITY_TOASTS_KEY, on ? '1' : '0');
  } catch {
    // Storage blocked: the switch simply does not stick.
  }
  toastListeners.forEach((l) => l());
}

export function subscribeActivityToasts(listener: () => void): () => void {
  toastListeners.add(listener);
  window.addEventListener('storage', listener);
  return () => {
    toastListeners.delete(listener);
    window.removeEventListener('storage', listener);
  };
}

/** The switch as a hook, for the tray. */
export function useActivityToasts(): [boolean, (on: boolean) => void] {
  const on = useSyncExternalStore(subscribeActivityToasts, getActivityToasts, () => true);
  return [on, setActivityToasts];
}

const ACTIVE = new Set(['waiting', 'running', 'estimating']);
const QUIET_SUCCESS = new Set(['mediaPrep', 'mediaAnalysis']);

export const isActiveState = (state: string): boolean => ACTIVE.has(state);
export const isFinishedState = (state: string): boolean => state === 'done' || state === 'failed' || state === 'cancelled';

/** The job's one-line name: its type label, plus the user-visible title when there is one. */
export function jobLabel(job: ActivityJob, t: T): string {
  const base = t(getJobType(job.type).labelKey);
  return job.title && !job.quietTitle ? `${base} (${job.title})` : base;
}

const stripQuery = (href: string): string => href.split(/[?#]/)[0].replace(/(.)\/$/, '$1');

const EDITOR_JOB_TYPES = new Set(['objectAnalysis', 'proposal', 'render', 'preview', 'loudness', 'audition', 'audioCleanup', 'subjectClip']);

/** True when the user is on the job's page, or on any timeline of the job's project for an editor job. */
export function isOnJobScreen(pathname: string | null, job: ActivityJob, href: string | null): boolean {
  if (isOnJobPage(pathname, href)) return true;
  if (!pathname || !job.projectId || !EDITOR_JOB_TYPES.has(job.type)) return false;
  return pathname.startsWith(`/editor/${job.projectId}/`) || pathname === `/editor/${job.projectId}`;
}

/** True when the user is already looking at the job's page (pathname without basePath, query ignored). */
export function isOnJobPage(pathname: string | null, href: string | null): boolean {
  if (!pathname || !href) return false;
  return stripQuery(href) === stripQuery(pathname);
}

function outcomeText(job: ActivityJob, label: string, t: T): string {
  if (job.state === 'needsReview') return t('toast.needsReview', { label });
  if (job.state === 'done') return t('toast.done', { label });
  if (job.state === 'cancelled') return t('toast.cancelled', { label });
  const refund = job.failure?.refunded === true ? ` ${t('toast.refunded')}` : '';
  return `${t('toast.failed', { label })}${refund}`;
}

const SESSION_START = Date.now();

/**
 * Tells the user when a job THEY started finishes while they are elsewhere. Only transitions
 * observed in this page session notify; whatever was already finished at first load is baseline.
 * Rendered by the provider, keyed by workspace so a switch starts a new baseline.
 */
export function FinishNotifier({ jobs, loaded }: { jobs: ActivityJob[]; loaded: boolean }): null {
  const t = useT('activity');
  const pathname = usePathname();
  const router = useRouter();
  const seen = useRef(new Map<string, string>());
  const baselined = useRef(false);

  useEffect(() => {
    const prev = seen.current;
    for (const job of jobs) {
      const before = prev.get(job.id);
      prev.set(job.id, job.state);
      if (job.origin === 'server' && !baselined.current) continue;
      const review = job.state === 'needsReview';
      if (!review && !isFinishedState(job.state)) continue;
      if (review && before === 'needsReview') continue;
      const wasActive = before !== undefined && isActiveState(before);
      const fresh = before === undefined && (job.origin === 'client' || Date.parse(job.createdAt) >= SESSION_START);
      if (!review && !wasActive && !fresh) continue;
      if (before !== undefined && isFinishedState(before)) continue;
      if (!job.startedByYou) continue;
      if (getJobType(job.type).notifyOnFinish === false) continue;
      if (job.state === 'done' && QUIET_SUCCESS.has(job.type)) continue;
      const href = jobHref(job);
      // A success is quiet anywhere in the editor of its project; a failure or cancel is quiet only on its own page.
      if (!review && (job.state === 'done' ? isOnJobScreen(pathname, job, href) : isOnJobPage(pathname, href))) continue;
      const label = jobLabel(job, t);
      const text = outcomeText(job, label, t);
      if (getActivityToasts()) {
        notifications.show({
          id: `activity-${job.id}`,
          color: job.state === 'failed' ? 'red' : job.state === 'done' ? 'green' : review ? 'yellow' : 'gray',
          autoClose: 8000,
          message: createElement(
            'span',
            null,
            text,
            href ? ' ' : null,
            href ? createElement(Link, { href, style: { textDecoration: 'underline' } }, t('toast.open')) : null,
          ),
        });
      }
      if (typeof document !== 'undefined' && document.hidden && getNotifySwitch() && notificationPermission() === 'granted') {
        showAssistantNotification({
          title: t('toast.browserTitle'),
          body: text,
          threadId: job.id,
          onClick: () => {
            if (href) router.push(href);
          },
        });
      }
    }
    if (loaded) baselined.current = true;
  }, [jobs, loaded, pathname, router, t]);

  return null;
}
